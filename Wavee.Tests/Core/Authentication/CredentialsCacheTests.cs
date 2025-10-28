using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Wavee.Core.Authentication;
using Wavee.Protocol;
using Wavee.Tests.Helpers;
using Xunit;

namespace Wavee.Tests.Core.Authentication;

/// <summary>
/// Tests for CredentialsCache class.
/// Validates file-based credential storage, encryption, and error handling.
/// </summary>
public class CredentialsCacheTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly CredentialsCache _cache;

    public CredentialsCacheTests()
    {
        // Create a unique temp directory for each test instance
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"wavee-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);

        // Create cache instance with temp directory
        _cache = new CredentialsCache(_tempDirectory);
    }

    public void Dispose()
    {
        // Clean up: delete temp directory and all contents
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    [Fact]
    public async Task SaveCredentialsAsync_ShouldCreateCacheFile()
    {
        // ============================================================
        // WHY: Save operation must create a file in the cache
        //      directory that can be verified to exist.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials("testuser");

        // Act
        await _cache.SaveCredentialsAsync(credentials);

        // Assert
        var files = Directory.GetFiles(_tempDirectory, "*_credentials.dat");
        files.Should().NotBeEmpty("cache file should be created");
        files.Should().ContainSingle("exactly one cache file should exist");
    }

    [Fact]
    public async Task LoadCredentialsAsync_WhenFileNotExists_ShouldReturnNull()
    {
        // ============================================================
        // WHY: Loading non-existent credentials should return null
        //      rather than throwing an exception.
        // ============================================================

        // Act
        var result = await _cache.LoadCredentialsAsync("nonexistent");

        // Assert
        result.Should().BeNull("no cached credentials should exist");
    }

    [Fact]
    public async Task SaveLoadRoundtrip_ShouldPreserveAllProperties()
    {
        // ============================================================
        // WHY: CRITICAL - Credentials must survive serialization,
        //      encryption, decryption, and deserialization without
        //      data loss or corruption.
        // ============================================================

        // Arrange
        var originalCredentials = new Credentials
        {
            Username = "testuser123",
            AuthType = AuthenticationType.AuthenticationStoredSpotifyCredentials,
            AuthData = TestHelpers.GenerateRandomBytes(32)
        };

        // Act
        await _cache.SaveCredentialsAsync(originalCredentials);
        var loadedCredentials = await _cache.LoadCredentialsAsync(originalCredentials.Username);

        // Assert
        loadedCredentials.Should().NotBeNull("credentials should be loaded");
        loadedCredentials!.Username.Should().Be(originalCredentials.Username);
        loadedCredentials.AuthType.Should().Be(originalCredentials.AuthType);
        loadedCredentials.AuthData.Should().BeEquivalentTo(originalCredentials.AuthData,
            "auth data must be preserved byte-for-byte");
    }

    [Fact]
    public async Task ClearCredentialsAsync_ShouldDeleteCacheFile()
    {
        // ============================================================
        // WHY: Clear operation must remove credentials file
        //      from disk for security (logout scenario).
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials("testuser");
        await _cache.SaveCredentialsAsync(credentials);

        // Verify file exists before clearing
        var filesBeforeClear = Directory.GetFiles(_tempDirectory, "*_credentials.dat");
        filesBeforeClear.Should().NotBeEmpty("cache file should exist before clear");

        // Act
        await _cache.ClearCredentialsAsync(credentials.Username);

        // Assert
        var filesAfterClear = Directory.GetFiles(_tempDirectory, "*testuser*");
        filesAfterClear.Should().BeEmpty("cache file should be deleted");
    }

    [Fact]
    public async Task Constructor_ShouldCreateCacheDirectory()
    {
        // ============================================================
        // WHY: Cache directory must be created automatically
        //      if it doesn't exist (first-run scenario).
        // ============================================================

        // Arrange - create a path that doesn't exist yet
        var nonExistentPath = Path.Combine(_tempDirectory, "subdir", "cache");
        Directory.Exists(nonExistentPath).Should().BeFalse("directory should not exist initially");

        // Act
        _ = new CredentialsCache(nonExistentPath);

        // Assert
        Directory.Exists(nonExistentPath).Should().BeTrue(
            "constructor should create cache directory");
    }

    [Fact]
    public async Task LoadCredentialsAsync_WithCorruptedData_ShouldReturnNull()
    {
        // ============================================================
        // WHY: Corrupted cache files (disk errors, manual editing)
        //      should not crash the application - return null instead.
        // ============================================================

        // Arrange
        var credentials = ProtobufHelpers.CreateValidCredentials("testuser");
        await _cache.SaveCredentialsAsync(credentials);

        // Corrupt the cache file by writing garbage data
        var cacheFile = Directory.GetFiles(_tempDirectory, "*testuser*")[0];
        await File.WriteAllBytesAsync(cacheFile, new byte[] { 0xFF, 0xFE, 0xFD, 0xFC });

        // Act
        var result = await _cache.LoadCredentialsAsync(credentials.Username);

        // Assert
        result.Should().BeNull("corrupted cache should return null, not throw");
    }

    [Fact]
    public async Task SaveCredentialsAsync_WithMultipleUsers_ShouldCreateSeparateFiles()
    {
        // ============================================================
        // WHY: Each user's credentials must be stored in a separate
        //      file to support multi-account scenarios.
        // ============================================================

        // Arrange
        var credentials1 = ProtobufHelpers.CreateValidCredentials("alice");
        var credentials2 = ProtobufHelpers.CreateValidCredentials("bob");

        // Act
        await _cache.SaveCredentialsAsync(credentials1);
        await _cache.SaveCredentialsAsync(credentials2);

        // Assert
        var files = Directory.GetFiles(_tempDirectory, "*_credentials.dat");
        files.Should().HaveCount(2, "each user should have their own cache file");

        // Verify both can be loaded independently
        var loadedAlice = await _cache.LoadCredentialsAsync("alice");
        var loadedBob = await _cache.LoadCredentialsAsync("bob");

        loadedAlice.Should().NotBeNull();
        loadedBob.Should().NotBeNull();
        loadedAlice!.Username.Should().Be("alice");
        loadedBob!.Username.Should().Be("bob");
    }
}
