using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wavee.Core.Authentication;

/// <summary>
/// Interface for storing and retrieving cached credentials.
/// </summary>
public interface ICredentialsCache
{
    /// <summary>
    /// Loads cached credentials for the specified username.
    /// </summary>
    /// <param name="username">The Spotify username (or null for default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached credentials, or null if not found or invalid.</returns>
    Task<Credentials?> LoadCredentialsAsync(string? username = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves credentials to the cache.
    /// </summary>
    /// <param name="credentials">The credentials to save.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveCredentialsAsync(Credentials credentials, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears cached credentials.
    /// </summary>
    /// <param name="username">The Spotify username (or null for default).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearCredentialsAsync(string? username = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the last authenticated username.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last username, or null if not found.</returns>
    Task<string?> LoadLastUsernameAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default credentials cache implementation that stores encrypted credentials in the user's app data folder.
/// Uses DPAPI (Data Protection API) on Windows for encryption, falls back to unencrypted on other platforms.
/// </summary>
public sealed class CredentialsCache : ICredentialsCache
{
    private readonly string _cacheDirectory;
    private readonly ILogger? _logger;
    private readonly bool _useEncryption;

    /// <summary>
    /// Creates a new credentials cache.
    /// </summary>
    /// <param name="cacheDirectory">
    /// Directory to store credentials files. If null, uses default location
    /// (%APPDATA%/Wavee/credentials on Windows, ~/.wavee/credentials on Unix).
    /// </param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public CredentialsCache(string? cacheDirectory = null, ILogger? logger = null)
    {
        _cacheDirectory = cacheDirectory ?? GetDefaultCacheDirectory();
        _logger = logger;

        // DPAPI is only available on Windows
        _useEncryption = OperatingSystem.IsWindows();

        if (!_useEncryption)
        {
            _logger?.LogWarning(
                "Credentials encryption not available on this platform. Credentials will be stored unencrypted.");
        }

        // Ensure cache directory exists
        Directory.CreateDirectory(_cacheDirectory);
    }

    /// <inheritdoc/>
    public async Task<Credentials?> LoadCredentialsAsync(
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        var filePath = GetCredentialsFilePath(username);

        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("No cached credentials found at {FilePath}", filePath);
            return null;
        }

        try
        {
            _logger?.LogDebug("Loading cached credentials from {FilePath}", filePath);

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

            // Deserialize (using source-generated context for AOT)
            var json = Encoding.UTF8.GetString(decryptedData);
            var credentials = JsonSerializer.Deserialize(json, AuthenticationJsonSerializerContext.Default.Credentials);

            if (credentials == null)
            {
                _logger?.LogWarning("Failed to deserialize credentials from {FilePath}", filePath);
                return null;
            }

            _logger?.LogInformation("Loaded cached credentials for user: {Username}", credentials.Username ?? "<unknown>");
            return credentials;
        }
        catch (CryptographicException ex)
        {
            _logger?.LogError(ex, "Failed to decrypt credentials (may be corrupted)");
            return null;
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse credentials JSON");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error loading credentials");
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SaveCredentialsAsync(
        Credentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var filePath = GetCredentialsFilePath(credentials.Username);

        try
        {
            _logger?.LogDebug("Saving credentials to {FilePath}", filePath);

            // Serialize (using source-generated context for AOT)
            var json = JsonSerializer.Serialize(credentials, AuthenticationJsonSerializerContext.Default.Credentials);
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

            _logger?.LogInformation("Saved credentials to cache for user: {Username}", credentials.Username ?? "<unknown>");

            // Save last username for future loads
            if (!string.IsNullOrWhiteSpace(credentials.Username))
            {
                await SaveLastUsernameAsync(credentials.Username, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save credentials to cache");
            throw new AuthenticationException(
                AuthenticationFailureReason.ProtocolError,
                "Failed to save credentials to cache",
                ex);
        }
    }

    /// <inheritdoc/>
    public Task ClearCredentialsAsync(string? username = null, CancellationToken cancellationToken = default)
    {
        var filePath = GetCredentialsFilePath(username);

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger?.LogInformation("Cleared cached credentials at {FilePath}", filePath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear cached credentials");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<string?> LoadLastUsernameAsync(CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_cacheDirectory, "last_user.txt");

        if (!File.Exists(filePath))
        {
            _logger?.LogDebug("No last username file found at {FilePath}", filePath);
            return null;
        }

        try
        {
            var username = await File.ReadAllTextAsync(filePath, cancellationToken);
            _logger?.LogDebug("Loaded last username: {Username}", username);
            return string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load last username");
            return null;
        }
    }

    /// <summary>
    /// Saves the last authenticated username.
    /// </summary>
    private async Task SaveLastUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_cacheDirectory, "last_user.txt");

        try
        {
            await File.WriteAllTextAsync(filePath, username, cancellationToken);
            _logger?.LogDebug("Saved last username: {Username}", username);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save last username (non-critical)");
            // Don't throw - this is a convenience feature, not critical
        }
    }

    /// <summary>
    /// Gets the file path for a credentials cache file.
    /// </summary>
    private string GetCredentialsFilePath(string? username)
    {
        var fileName = string.IsNullOrWhiteSpace(username)
            ? "default_credentials.dat"
            : $"{SanitizeFilename(username)}_credentials.dat";

        return Path.Combine(_cacheDirectory, fileName);
    }

    /// <summary>
    /// Gets the default cache directory based on platform.
    /// </summary>
    private static string GetDefaultCacheDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Wavee", "credentials");
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
