using System.Text.Json;

namespace Wavee.Connect.Protocol;

/// <summary>
/// Dealer message (MESSAGE type).
/// </summary>
public sealed record DealerMessage
{
    public required string Uri { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required byte[] Payload { get; init; }
}

/// <summary>
/// Dealer request (REQUEST type).
/// </summary>
/// <remarks>
/// The <see cref="Key"/> field is used for replies and is treated as an opaque string.
/// <see cref="MessageId"/> and <see cref="SenderDeviceId"/> are extracted from the key
/// if it matches the standard "message_id/device_id" format, otherwise default values are used.
/// Only <see cref="Key"/> is required for sending replies - the other fields are optional metadata.
/// </remarks>
public sealed record DealerRequest
{
    /// <summary>
    /// Opaque request key used for sending replies. Can be any string format.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Message type identifier (e.g., "hm://connect-state/v1/...").
    /// </summary>
    public required string MessageIdent { get; init; }

    /// <summary>
    /// Message ID extracted from key if format is "id/device". Default is 0 for non-standard keys.
    /// This field is not used by reply logic - included for backward compatibility.
    /// </summary>
    public required int MessageId { get; init; }

    /// <summary>
    /// Sender device ID extracted from key if format is "id/device". Default is empty for non-standard keys.
    /// This field is not used by reply logic - included for backward compatibility.
    /// </summary>
    public required string SenderDeviceId { get; init; }

    /// <summary>
    /// Command payload as JSON element.
    /// </summary>
    public required JsonElement Command { get; init; }
}

/// <summary>
/// Raw dealer message from JSON (internal use only).
/// </summary>
internal sealed class RawDealerMessage
{
    public string Type { get; set; } = string.Empty;
    public string? Uri { get; set; }
    public string? Key { get; set; }
    public string? MessageIdent { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public string[]? Payloads { get; set; }
    public JsonElement? Payload { get; set; }
}
