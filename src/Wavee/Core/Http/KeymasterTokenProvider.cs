using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Wavee.Core.Mercury;
using Wavee.Core.Session;

namespace Wavee.Core.Http;

/// <summary>
/// Provides properly scoped OAuth tokens for Spotify's public Web API
/// by calling the Keymaster endpoint via Mercury protocol.
/// </summary>
/// <remarks>
/// Keymaster tokens are different from Login5 tokens:
/// - Login5 tokens: for internal spclient API (no scopes)
/// - Keymaster tokens: for public Web API with specific OAuth scopes
/// </remarks>
public sealed class KeymasterTokenProvider
{
    private readonly MercuryManager _mercury;
    private readonly SessionConfig _config;
    private readonly string _deviceId;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, CachedToken> _cache = new();

    public KeymasterTokenProvider(
        MercuryManager mercury, SessionConfig config, string deviceId, ILogger? logger = null)
    {
        _mercury = mercury ?? throw new ArgumentNullException(nameof(mercury));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        _logger = logger;
    }

    /// <summary>
    /// Gets an access token with the specified scopes for the Spotify Web API.
    /// Tokens are cached until near-expiry.
    /// </summary>
    /// <param name="scopes">Comma-separated scope list (e.g., "user-read-private,user-read-email")</param>
    public async Task<string> GetTokenAsync(string scopes, CancellationToken ct = default)
    {
        // Check cache
        if (_cache.TryGetValue(scopes, out var cached) &&
            DateTimeOffset.UtcNow < cached.Expiry.AddSeconds(-10))
        {
            return cached.Token;
        }

        // Fetch via Mercury → Keymaster
        var uri = $"hm://keymaster/token/authenticated?scope={scopes}&client_id={_config.GetClientId()}&device_id={_deviceId}";

        _logger?.LogDebug("Requesting Keymaster token: scopes={Scopes}", scopes);

        var response = await _mercury.GetAsync(uri, ct);

        if (response.Payload.Count == 0)
            throw new InvalidOperationException("Empty Keymaster response");

        var json = Encoding.UTF8.GetString(response.Payload[0]);
        var data = JsonSerializer.Deserialize(json, KeymasterResponseJsonContext.Default.KeymasterResponse)
            ?? throw new InvalidOperationException("Failed to parse Keymaster response");

        var expiry = DateTimeOffset.UtcNow.AddSeconds(data.ExpiresIn);
        _cache[scopes] = new CachedToken(data.AccessToken, expiry);

        _logger?.LogInformation("Keymaster token obtained: scopes={Scopes}, expires in {Seconds}s",
            scopes, data.ExpiresIn);

        return data.AccessToken;
    }

    private sealed record CachedToken(string Token, DateTimeOffset Expiry);
}

/// <summary>
/// JSON response from hm://keymaster/token/authenticated
/// </summary>
public sealed record KeymasterResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("tokenType")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("scope")]
    public IReadOnlyList<string>? Scope { get; init; }
}

[JsonSerializable(typeof(KeymasterResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class KeymasterResponseJsonContext : JsonSerializerContext
{
}
