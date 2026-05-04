using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to update context metadata (track list, restrictions, etc.).
/// </summary>
public sealed record UpdateContextCommand : ConnectCommand
{
    /// <summary>
    /// Context URI to update (e.g., playlist, album).
    /// </summary>
    public string? ContextUri { get; init; }

    /// <summary>
    /// Session ID for the update.
    /// </summary>
    public string? SessionId { get; init; }

    internal static UpdateContextCommand FromJson(DealerRequest request, JsonElement json)
    {
        var contextUri = json.TryGetProperty("context_uri", out var ctx)
            ? ctx.GetString() : null;
        var sessionId = json.TryGetProperty("session_id", out var sid)
            ? sid.GetString() : null;

        return new UpdateContextCommand
        {
            Endpoint = "update_context",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            ContextUri = contextUri,
            SessionId = sessionId
        };
    }
}
