using FluentAssertions;
using Wavee.Connect.Playback.Decoders;
using Xunit;

namespace Wavee.Tests.Connect.Playback.Decoders;

/// <summary>
/// Tests for SkipStream class.
/// Validates stream wrapper that skips header bytes (0xa7 for OGG files).
/// </summary>
public class SkipStreamTests
{
    private const long DefaultSkipBytes = 167; // 0xa7 for Spotify OGG files

    [Fact]
    public void Read_AtPosition0_ReadsFromOffset()
    {
        // ============================================================
        // WHY: Reading at position 0 should read from the skip offset.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        var buffer = new byte[10];

        // Act
        var bytesRead = skipStream.Read(buffer, 0, 10);

        // Assert
        bytesRead.Should().Be(10);
        // Data at offset 167 should be 167, 168, 169, ...
        buffer[0].Should().Be((byte)DefaultSkipBytes);
        buffer[1].Should().Be((byte)(DefaultSkipBytes + 1));
    }

    [Fact]
    public void Read_SpanOverload_ReadsFromOffset()
    {
        // ============================================================
        // WHY: Span-based Read should also respect the skip offset.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        Span<byte> buffer = stackalloc byte[10];

        // Act
        var bytesRead = skipStream.Read(buffer);

        // Assert
        bytesRead.Should().Be(10);
        buffer[0].Should().Be((byte)DefaultSkipBytes);
    }

    [Fact]
    public async Task ReadAsync_ReadsFromOffset()
    {
        // ============================================================
        // WHY: Async read should also respect the skip offset.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        var buffer = new byte[10];

        // Act
        var bytesRead = await skipStream.ReadAsync(buffer, 0, 10);

        // Assert
        bytesRead.Should().Be(10);
        buffer[0].Should().Be((byte)DefaultSkipBytes);
    }

    [Fact]
    public async Task ReadAsync_Memory_ReadsFromOffset()
    {
        // ============================================================
        // WHY: Memory-based async read should also respect the skip offset.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        var buffer = new byte[10];

        // Act
        var bytesRead = await skipStream.ReadAsync(buffer.AsMemory());

        // Assert
        bytesRead.Should().Be(10);
        buffer[0].Should().Be((byte)DefaultSkipBytes);
    }

    [Fact]
    public void Position_Get_ReturnsAdjustedPosition()
    {
        // ============================================================
        // WHY: Position should be adjusted to hide skipped bytes.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Assert - initial position should be 0 (not 167)
        skipStream.Position.Should().Be(0);

        // Act - read some data
        skipStream.Read(new byte[10], 0, 10);

        // Assert - position should be 10
        skipStream.Position.Should().Be(10);
    }

    [Fact]
    public void Position_Set_SetsAdjustedPosition()
    {
        // ============================================================
        // WHY: Setting position should add the skip offset.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Act
        skipStream.Position = 50;

        // Assert
        skipStream.Position.Should().Be(50);
        innerStream.Position.Should().Be(DefaultSkipBytes + 50);
    }

    [Fact]
    public void Position_SetNegative_ThrowsArgumentOutOfRange()
    {
        // ============================================================
        // WHY: Negative position should be rejected.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Act
        Action act = () => skipStream.Position = -1;

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Length_ReturnsAdjustedLength()
    {
        // ============================================================
        // WHY: Length should be reduced by the skip amount.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Assert
        skipStream.Length.Should().Be(300 - DefaultSkipBytes);
    }

    [Fact]
    public void Length_WhenSmallerThanSkip_ReturnsZero()
    {
        // ============================================================
        // WHY: If inner stream is smaller than skip, length should be 0.
        // ============================================================

        // Arrange
        var data = CreateTestData(100); // Less than 167
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Assert
        skipStream.Length.Should().Be(0);
    }

    [Fact]
    public void Seek_FromBegin_AddsOffset()
    {
        // ============================================================
        // WHY: Seeking from begin should add the skip offset.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Act
        var newPos = skipStream.Seek(50, SeekOrigin.Begin);

        // Assert
        newPos.Should().Be(50);
        skipStream.Position.Should().Be(50);
        innerStream.Position.Should().Be(DefaultSkipBytes + 50);
    }

    [Fact]
    public void Seek_FromCurrent_AddsRelativeOffset()
    {
        // ============================================================
        // WHY: Seeking from current should work relative to current position.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);
        skipStream.Position = 50;

        // Act
        var newPos = skipStream.Seek(20, SeekOrigin.Current);

        // Assert
        newPos.Should().Be(70);
    }

    [Fact]
    public void Seek_FromEnd_WorksCorrectly()
    {
        // ============================================================
        // WHY: Seeking from end should use adjusted length.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Act
        var newPos = skipStream.Seek(-10, SeekOrigin.End);

        // Assert
        // Length = 300 - 167 = 133, so -10 from end = 123
        newPos.Should().Be(skipStream.Length - 10);
    }

    [Fact]
    public void Seek_BeforeSkipRegion_SeekNearStart()
    {
        // ============================================================
        // WHY: Seeking near the start should work correctly.
        // ============================================================

        // Arrange
        var data = CreateTestData(300);
        using var innerStream = new MemoryStream(data);
        using var skipStream = new SkipStream(innerStream, DefaultSkipBytes);

        // Act - seek to position 5 from beginning
        var newPos = skipStream.Seek(5, SeekOrigin.Begin);

        // Assert
        newPos.Should().Be(5);
        skipStream.Position.Should().Be(5);
    }

    [Fact]
    public void CanSeek_InnerCanSeek_ReturnsTrue()
    {
        // ============================================================
        // WHY: CanSeek should reflect inner stream's capability.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        using var skipStream = new SkipStream(innerStream, 10);

        // Assert
        skipStream.CanSeek.Should().BeTrue();
    }

    [Fact]
    public void CanRead_InnerCanRead_ReturnsTrue()
    {
        // ============================================================
        // WHY: CanRead should reflect inner stream's capability.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        using var skipStream = new SkipStream(innerStream, 10);

        // Assert
        skipStream.CanRead.Should().BeTrue();
    }

    [Fact]
    public void CanWrite_AlwaysFalse()
    {
        // ============================================================
        // WHY: SkipStream is read-only.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        using var skipStream = new SkipStream(innerStream, 10);

        // Assert
        skipStream.CanWrite.Should().BeFalse();
    }

    [Fact]
    public void Write_Throws()
    {
        // ============================================================
        // WHY: Write operations should throw NotSupportedException.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        using var skipStream = new SkipStream(innerStream, 10);

        // Act
        Action act = () => skipStream.Write(new byte[10], 0, 10);

        // Assert
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void SetLength_Throws()
    {
        // ============================================================
        // WHY: SetLength is not supported on read-only stream.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        using var skipStream = new SkipStream(innerStream, 10);

        // Act
        Action act = () => skipStream.SetLength(50);

        // Assert
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Constructor_NullInnerStream_Throws()
    {
        // ============================================================
        // WHY: Null inner stream must be rejected.
        // ============================================================

        // Act
        Action act = () => new SkipStream(null!, 10);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NegativeSkipBytes_Throws()
    {
        // ============================================================
        // WHY: Negative skip bytes is invalid.
        // ============================================================

        // Act
        using var innerStream = new MemoryStream(CreateTestData(100));
        Action act = () => new SkipStream(innerStream, -1);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_SeeksToSkipPosition()
    {
        // ============================================================
        // WHY: Constructor should position inner stream at skip offset.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(200));
        innerStream.Position = 0; // Ensure at start

        // Act
        using var skipStream = new SkipStream(innerStream, 50);

        // Assert
        innerStream.Position.Should().Be(50);
    }

    [Fact]
    public void Dispose_LeaveOpen_DoesNotDisposeInner()
    {
        // ============================================================
        // WHY: With leaveOpen=true, inner stream should remain usable.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        var skipStream = new SkipStream(innerStream, 10, leaveOpen: true);

        // Act
        skipStream.Dispose();

        // Assert - inner stream should still be readable
        Action act = () => innerStream.ReadByte();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_NotLeaveOpen_DisposesInner()
    {
        // ============================================================
        // WHY: With leaveOpen=false (default), inner stream should be disposed.
        // ============================================================

        // Arrange
        var innerStream = new MemoryStream(CreateTestData(100));
        var skipStream = new SkipStream(innerStream, 10, leaveOpen: false);

        // Act
        skipStream.Dispose();

        // Assert - inner stream should be disposed
        Action act = () => innerStream.ReadByte();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_LeaveOpen_DoesNotDisposeInner()
    {
        // ============================================================
        // WHY: Async dispose with leaveOpen=true should not dispose inner.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        var skipStream = new SkipStream(innerStream, 10, leaveOpen: true);

        // Act
        await skipStream.DisposeAsync();

        // Assert
        Action act = () => innerStream.ReadByte();
        act.Should().NotThrow();
    }

    [Fact]
    public void Read_AfterDispose_Throws()
    {
        // ============================================================
        // WHY: Reading from disposed stream should throw.
        // ============================================================

        // Arrange
        var innerStream = new MemoryStream(CreateTestData(100));
        var skipStream = new SkipStream(innerStream, 10, leaveOpen: true);
        skipStream.Dispose();

        // Act
        Action act = () => skipStream.Read(new byte[10], 0, 10);

        // Assert
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Flush_DoesNotThrow()
    {
        // ============================================================
        // WHY: Flush is a no-op but should not throw.
        // ============================================================

        // Arrange
        using var innerStream = new MemoryStream(CreateTestData(100));
        using var skipStream = new SkipStream(innerStream, 10);

        // Act & Assert
        Action act = () => skipStream.Flush();
        act.Should().NotThrow();
    }

    private static byte[] CreateTestData(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(i % 256);
        }
        return data;
    }
}
