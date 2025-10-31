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
        var posMs = json.GetProperty("position").GetInt64();

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
