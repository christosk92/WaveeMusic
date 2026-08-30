using System.Text.Json.Serialization;

namespace Wavee.Sdk;

/// <summary>
/// What the host tells a module at <c>module/initialize</c>: who is hosting it, which protocol version was
/// negotiated, where it may write, and the app's current playback preferences.
/// </summary>
/// <param name="HostVersion">The app's informational version string.</param>
/// <param name="ProtocolVersion">The negotiated wire-protocol version (inside the host's advertised range).</param>
/// <param name="DataDir">The module's private, writable directory (<c>%LOCALAPPDATA%\Wavee\modules-data\&lt;id&gt;</c>).</param>
/// <param name="Locale">BCP-47 locale the app is running in.</param>
/// <param name="Prefs">The app's playback preferences, or null when it did not send any.</param>
/// <param name="CacheBudgetBytes">How many bytes the module may keep on disk; 0 means "no budget declared".</param>
public sealed record ModuleContext(
    string HostVersion,
    int ProtocolVersion,
    string DataDir,
    string Locale,
    ResolvePreferences? Prefs,
    long CacheBudgetBytes);

/// <summary>Severity of a <c>module/log</c> line; the host maps it onto its own logger.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModuleLogLevel>))]
public enum ModuleLogLevel
{
    /// <summary>Verbose developer detail.</summary>
    [JsonStringEnumMemberName("debug")] Debug,

    /// <summary>Normal operational detail.</summary>
    [JsonStringEnumMemberName("info")] Info,

    /// <summary>Something recoverable went wrong.</summary>
    [JsonStringEnumMemberName("warn")] Warn,

    /// <summary>Something failed.</summary>
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>A bearer token the host holds for a provider it owns (answer to <c>host/auth/token</c>).</summary>
/// <param name="AccessToken">The bearer token value.</param>
/// <param name="ExpiresAtUnixMs">Absolute expiry in Unix milliseconds; 0 means "unknown".</param>
public sealed record AuthToken(string AccessToken, long ExpiresAtUnixMs);

/// <summary>The non-secret session identity a module needs to talk to a provider's API (answer to <c>host/auth/context</c>).</summary>
/// <param name="DeviceId">The app's device id for that provider.</param>
/// <param name="ClientToken">The provider's client token, when the host holds one.</param>
/// <param name="SpclientBaseUrl">Base url of the provider's API endpoint the host is currently using.</param>
/// <param name="Session">The signed-in session's attributes, when there is one.</param>
public sealed record AuthContext(string DeviceId, string? ClientToken, string? SpclientBaseUrl, AuthSession? Session);

/// <summary>The signed-in account's attributes, as the host knows them.</summary>
/// <param name="Account">Account/user name.</param>
/// <param name="Market">Two-letter market code.</param>
/// <param name="Catalogue">Catalogue token (e.g. <c>premium</c>).</param>
/// <param name="Locale">The account's locale.</param>
/// <param name="Tier">Subscription tier.</param>
/// <param name="ExplicitFilter">True when explicit content is filtered out for this account.</param>
public sealed record AuthSession(
    string? Account,
    string? Market,
    string? Catalogue,
    string? Locale,
    string? Tier,
    bool ExplicitFilter);
