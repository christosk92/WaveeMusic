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
public sealed record DealerRequest
{
    public required string Key { get; init; }
    public required string MessageIdent { get; init; }
    public required int MessageId { get; init; }
    public required string SenderDeviceId { get; init; }
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
