using System.Text.Json.Serialization;

namespace Wavee.Sdk;

/// <summary>
/// Why a module could not do what was asked. Doubles as the JSON-RPC error code (the numeric value) and as the
/// <c>data.kind</c> discriminator (the camel-case name), which the host maps onto its own failure reasons.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModuleErrorCode>))]
public enum ModuleErrorCode
{
    /// <summary>The module does not own this input/uri at all; the router should keep looking.</summary>
    [JsonStringEnumMemberName("notOwned")] NotOwned = 1001,

    /// <summary>The content exists but cannot be served (private, deleted, region rules, SABR-only, …).</summary>
    [JsonStringEnumMemberName("unavailable")] Unavailable,

    /// <summary>Credentials are missing or expired; the user has to sign in or complete setup.</summary>
    [JsonStringEnumMemberName("needsAuth")] NeedsAuth,

    /// <summary>A temporary failure; retrying later (optionally after <c>retryAfterMs</c>) may work.</summary>
    [JsonStringEnumMemberName("transient")] Transient,

    /// <summary>The source is offline right now (a channel that is not broadcasting, a dead station).</summary>
    [JsonStringEnumMemberName("offline")] Offline,

    /// <summary>Blocked in this region.</summary>
    [JsonStringEnumMemberName("geoBlocked")] GeoBlocked,

    /// <summary>The request itself is not supported (unknown capability, unsupported protocol version).</summary>
    [JsonStringEnumMemberName("unsupported")] Unsupported,
}

/// <summary>
/// The exception a module throws to produce a typed JSON-RPC error. <see cref="ModuleRunner"/> converts it into
/// <c>{ code, message, data: { kind, retryAfterMs?, detail? } }</c>; the host raises the same exception on its side.
/// </summary>
/// <param name="code">The typed failure reason.</param>
/// <param name="message">Human-readable message (surfaces in logs and, for some kinds, in the UI).</param>
public sealed class ModuleException(ModuleErrorCode code, string message) : Exception(message)
{
    /// <summary>The typed failure reason.</summary>
    public ModuleErrorCode Code => code;

    /// <summary>Optional hint for how long to wait before retrying.</summary>
    public int? RetryAfterMs { get; init; }

    /// <summary>Optional machine-readable detail (an upstream status token, an http code, …).</summary>
    public string? Detail { get; init; }
}
