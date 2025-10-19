using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.OAuth;

/// <summary>
/// Interface for OAuth token storage and retrieval.
/// </summary>
public interface ITokenCache
{
    /// <summary>
    /// Loads a cached token for the specified username.
    /// </summary>
    /// <param name="username">The Spotify username (or null for default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached token, or null if not found or invalid.</returns>
    Task<OAuthToken?> LoadTokenAsync(string? username = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a token to the cache.
    /// </summary>
    /// <param name="token">The token to save.</param>
    /// <param name="username">The Spotify username (or null for default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveTokenAsync(OAuthToken token, string? username = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears a cached token.
    /// </summary>
    /// <param name="username">The Spotify username (or null for default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearTokenAsync(string? username = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default token cache implementation that stores encrypted tokens in the user's app data folder.
/// Uses DPAPI (Data Protection API) on Windows for encryption, falls back to unencrypted on other platforms.
/// </summary>
public sealed class TokenCache : ITokenCache
{
    private readonly string _cacheDirectory;
    private readonly ILogger? _logger;
    private readonly bool _useEncryption;

    /// <summary>
    /// Creates a new token cache.
    /// </summary>
    /// <param name="cacheDirectory">
    /// Directory to store token files. If null, uses default location
    /// (%APPDATA%/Wavee/tokens on Windows, ~/.wavee/tokens on Unix).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public TokenCache(string? cacheDirectory = null, ILogger? logger = null)
    {
        _cacheDirectory = cacheDirectory ?? GetDefaultCacheDirectory();
        _logger = logger;

        // DPAPI is only available on Windows
        _useEncryption = OperatingSystem.IsWindows();

        if (!_useEncryption)
        {
            _logger?.LogWarning(
                "Token encryption not available on this platform. Tokens will be stored unencrypted.");
        }

        // Ensure cache directory exists
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <inheritdoc/>
    public async Task<OAuthToken?> LoadTokenAsync(
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetTokenFilePath(username);

        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("No cached token found at {FilePath}", filePath);
            return null;
        }

        try
        {
            _logger?.LogDebug("Loading cached token from {FilePath}", filePath);

            // Read file
            var encryptedData = await File.ReadAllBytesAsync(filePath, cancellationToken);

            // Decrypt (if encryption is available)
            byte[] decryptedData;
            if (_useEncryption)
            {
                decryptedData = ProtectedData.Unprotect(
                    encryptedData,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
            }
            else
            {
                decryptedData = encryptedData;
            }

            // Deserialize
            var json = Encoding.UTF8.GetString(decryptedData);
            var token = JsonSerializer.Deserialize<OAuthToken>(json);

            if (token == null)
            {
                _logger?.LogWarning("Failed to deserialize token from {FilePath}", filePath);
                return null;
            }

            _logger?.LogInformation("Loaded cached token (expires {ExpiresAt})", token.ExpiresAt);
            return token;
        }
        catch (CryptographicException ex)
        {
            _logger?.LogError(ex, "Failed to decrypt token (may be corrupted)");
            return null;
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse token JSON");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error loading token");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SaveTokenAsync(
        OAuthToken token,
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        var filePath = GetTokenFilePath(username);

        try
        {
            _logger?.LogDebug("Saving token to {FilePath}", filePath);

            // Serialize
            var json = JsonSerializer.Serialize(token, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            // Encrypt (if available)
            byte[] dataToWrite;
            if (_useEncryption)
            {
                dataToWrite = ProtectedData.Protect(
                    jsonBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
            }
            else
            {
                dataToWrite = jsonBytes;
            }

            // Write to file
            await File.WriteAllBytesAsync(filePath, dataToWrite, cancellationToken);

            _logger?.LogInformation("Saved token to cache (expires {ExpiresAt})", token.ExpiresAt);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save token to cache");
            throw new OAuthException(
                OAuthFailureReason.Unknown,
                "Failed to save token to cache",
                ex);
        }
    }

    /// <inheritdoc/>
    public Task ClearTokenAsync(string? username = null, CancellationToken cancellationToken = default)
    {
        var filePath = GetTokenFilePath(username);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger?.LogInformation("Cleared cached token at {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear cached token");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the file path for a token cache file.
    /// </summary>
    private string GetTokenFilePath(string? username)
    {
        var fileName = string.IsNullOrWhiteSpace(username)
            ? "default_token.dat"
            : $"{SanitizeFilename(username)}_token.dat";

        return Path.Combine(_cacheDirectory, fileName);
    }

    /// <summary>
    /// Gets the default cache directory based on platform.
    /// </summary>
    private static string GetDefaultCacheDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Wavee", "tokens");
    }

    /// <summary>
    /// Sanitizes a username for use in a filename.
    /// </summary>
    private static string SanitizeFilename(string username)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder();

        foreach (var c in username)
        {
            sanitized.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sanitized.ToString();
    }
}
