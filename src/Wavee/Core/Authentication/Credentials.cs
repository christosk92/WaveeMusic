using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wavee.Protocol;

namespace Wavee.Core.Authentication;

/// <summary>
/// Credentials used to authenticate with Spotify Access Point.
/// </summary>
/// <remarks>
/// This record supports three authentication methods:
/// <list type="number">
/// <item>Username/Password - Initial login with account credentials</item>
/// <item>Access Token - OAuth-based login with Spotify token</item>
/// <item>Encrypted Blob - Reusable credentials from previous successful login</item>
/// </list>
///
/// Credentials are immutable and can be serialized to JSON for caching to disk.
/// After successful authentication, Spotify returns reusable credentials (blob) that
/// can be cached and used for future logins without requiring password/token again.
/// </remarks>
public sealed record Credentials
{
    /// <summary>
    /// Username (email or Spotify username). Null for token-based authentication.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>
    /// Authentication type from Protocol.Authentication.AuthenticationType enum.
    /// </summary>
    [JsonPropertyName("authType")]
    [JsonConverter(typeof(JsonStringEnumConverter<AuthenticationType>))]
    public AuthenticationType AuthType { get; init; }

    /// <summary>
    /// Authentication data (password bytes, token bytes, or decrypted blob).
    /// </summary>
    [JsonPropertyName("authData")]
    [JsonConverter(typeof(Base64ByteArrayConverter))]
    public byte[] AuthData { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Internal constructor - use factory methods instead.
    /// </summary>
    [JsonConstructor]
    internal Credentials() { }

    /// <summary>
    /// Creates credentials for username/password authentication.
    /// </summary>
    /// <param name="username">Spotify username or email address.</param>
    /// <param name="password">Account password.</param>
    /// <returns>Credentials configured for password authentication.</returns>
    /// <exception cref="ArgumentException">Thrown if username or password is null or whitespace.</exception>
    [Obsolete("Username/password authentication is no longer supported by Spotify. Use WithAccessToken() or WithBlob() instead.", true)]
    public static Credentials WithPassword(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        return new Credentials
        {
            Username = username,
            AuthType = AuthenticationType.AuthenticationUserPass,
            AuthData = Encoding.UTF8.GetBytes(password)
        };
    }

    /// <summary>
    /// Creates credentials for OAuth access token authentication.
    /// </summary>
    /// <param name="token">OAuth access token from Spotify.</param>
    /// <returns>Credentials configured for token authentication.</returns>
    /// <exception cref="ArgumentException">Thrown if token is null or whitespace.</exception>
    public static Credentials WithAccessToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return new Credentials
        {
            Username = null,  // No username for token auth
            AuthType = AuthenticationType.AuthenticationSpotifyToken,
            AuthData = Encoding.UTF8.GetBytes(token)
        };
    }

    /// <summary>
    /// Creates credentials from an encrypted reusable blob (from previous login).
    /// </summary>
    /// <param name="username">Spotify username.</param>
    /// <param name="encryptedBlob">Base64-encoded encrypted blob from cache.</param>
    /// <param name="deviceId">Device ID used to encrypt the blob.</param>
    /// <returns>Credentials with decrypted authentication data.</returns>
    /// <exception cref="ArgumentException">Thrown if parameters are invalid.</exception>
    /// <exception cref="AuthenticationException">Thrown if blob decryption fails.</exception>
    public static Credentials WithBlob(
        string username,
        ReadOnlySpan<byte> encryptedBlob,
        ReadOnlySpan<byte> deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        if (encryptedBlob.IsEmpty)
            throw new AuthenticationException(
                AuthenticationFailureReason.InvalidBlob,
                "Encrypted blob is empty");

        if (deviceId.IsEmpty)
            throw new AuthenticationException(
                AuthenticationFailureReason.InvalidBlob,
                "Device ID is empty");

        return BlobDecryptor.Decrypt(username, encryptedBlob, deviceId);
    }

    /// <summary>
    /// Returns a string representation of the credentials without revealing sensitive data.
    /// </summary>
    /// <returns>Safe string representation for logging.</returns>
    public override string ToString() =>
        $"Credentials {{ Username = {Username ?? "<token>"}, AuthType = {AuthType} }}";
}

/// <summary>
/// JSON converter for serializing byte arrays as Base64 strings.
/// </summary>
internal sealed class Base64ByteArrayConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var base64String = reader.GetString();
        return base64String != null ? Convert.FromBase64String(base64String) : Array.Empty<byte>();
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Convert.ToBase64String(value));
    }
}
