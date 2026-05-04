using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to seek to position in current track.
/// </summary>
public sealed record SeekCommand : ConnectCommand
{
    /// <summary>
    /// Target position in milliseconds.
    /// </summary>
    public required long PositionMs { get; init; }

    internal static SeekCommand FromJson(DealerRequest request, JsonElement json)
    {
        // Handle both formats:
        // 1. Legacy: { "position": 12345 }
        // 2. Real Spotify: { "value": 12345 } or { "value": "12345" }
        long posMs = 0;

        if (json.TryGetProperty("position", out var positionProp))
        {
            posMs = positionProp.GetInt64();
        }
        else if (json.TryGetProperty("value", out var valueProp))
        {
            // Value can be number or string
            if (valueProp.ValueKind == JsonValueKind.Number)
                posMs = valueProp.GetInt64();
            else if (valueProp.ValueKind == JsonValueKind.String)
                long.TryParse(valueProp.GetString(), out posMs);
        }

        return new SeekCommand
        {
            Endpoint = "seek_to",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            PositionMs = posMs
        };
    }
}
