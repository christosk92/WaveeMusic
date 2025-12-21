using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Base record for all Spotify Connect commands.
/// </summary>
public abstract record ConnectCommand
{
    /// <summary>
    /// Command endpoint (e.g., "play", "pause", "skip_next").
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Message identifier from dealer request.
    /// </summary>
    public required string MessageIdent { get; init; }

    /// <summary>
    /// Message ID for reply tracking.
    /// </summary>
    public required int MessageId { get; init; }

    /// <summary>
    /// Device ID that sent this command.
    /// </summary>
    public required string SenderDeviceId { get; init; }

    /// <summary>
    /// Reply key for sending responses back to dealer.
    /// Format: "{messageId}/{senderDeviceId}"
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Parses a DealerRequest into appropriate ConnectCommand subtype.
    /// </summary>
    /// <param name="request">Dealer request to parse.</param>
    /// <returns>Parsed command or null if unsupported.</returns>
    /// <remarks>
    /// Handles two formats:
    /// 1. Legacy/test format: endpoint in URL path (hm://connect-state/v1/play)
    /// 2. Real Spotify format: endpoint in JSON body (hm://connect-state/v1/player/command + command.endpoint)
    /// </remarks>
    public static ConnectCommand? Parse(DealerRequest request)
    {
        // Extract endpoint from message_ident
        // Format: "hm://connect-state/v1/{endpoint}" or "hm://connect-state/v1/player/command"
        var parts = request.MessageIdent.Split('/');
        if (parts.Length < 5) return null;  // Need at least 5 parts for hm://connect-state/v1/{endpoint}

        var urlEndpoint = parts[^1];  // Get last element
        string endpoint;
        var command = request.Command;

        // Real Spotify format: hm://connect-state/v1/player/command
        // The payload contains a nested "command" object with the actual endpoint
        // Structure: { "message_id": ..., "sent_by_device_id": ..., "command": { "endpoint": "play", ... } }
        if (urlEndpoint == "command" && parts.Length >= 6 && parts[^2] == "player")
        {
            // First extract the nested "command" object from payload
            if (!command.TryGetProperty("command", out var innerCommand))
                return null;
            // Then get the endpoint from the inner command
            if (!innerCommand.TryGetProperty("endpoint", out var endpointProp))
                return null;
            endpoint = endpointProp.GetString()?.ToLowerInvariant() ?? "";
            command = innerCommand;  // Use inner command for subsequent parsing
        }
        else
        {
            // Legacy/test format: endpoint in URL path
            endpoint = urlEndpoint;
        }

        return endpoint switch
        {
            "play" => PlayCommand.FromJson(request, command),
            "pause" => new PauseCommand
            {
                Endpoint = endpoint,
                MessageIdent = request.MessageIdent,
                MessageId = request.MessageId,
                SenderDeviceId = request.SenderDeviceId,
                Key = request.Key
            },
            "resume" => new ResumeCommand
            {
                Endpoint = endpoint,
                MessageIdent = request.MessageIdent,
                MessageId = request.MessageId,
                SenderDeviceId = request.SenderDeviceId,
                Key = request.Key
            },
            "seek_to" => SeekCommand.FromJson(request, command),
            "skip_next" => new SkipNextCommand
            {
                Endpoint = endpoint,
                MessageIdent = request.MessageIdent,
                MessageId = request.MessageId,
                SenderDeviceId = request.SenderDeviceId,
                Key = request.Key
            },
            "skip_prev" => new SkipPrevCommand
            {
                Endpoint = endpoint,
                MessageIdent = request.MessageIdent,
                MessageId = request.MessageId,
                SenderDeviceId = request.SenderDeviceId,
                Key = request.Key
            },
            "set_shuffling_context" => ShuffleCommand.FromJson(request, command),
            "set_repeating_context" => RepeatContextCommand.FromJson(request, command),
            "set_repeating_track" => RepeatTrackCommand.FromJson(request, command),
            "transfer" => TransferCommand.FromJson(request, command),
            "set_queue" => SetQueueCommand.FromJson(request, command),
            "add_to_queue" => AddToQueueCommand.FromJson(request, command),
            "update_context" => UpdateContextCommand.FromJson(request, command),
            "set_options" => SetOptionsCommand.FromJson(request, command),
            _ => null // Unsupported command
        };
    }
}

/// <summary>
/// Command to pause playback.
/// </summary>
public sealed record PauseCommand : ConnectCommand;

/// <summary>
/// Command to resume playback.
/// </summary>
public sealed record ResumeCommand : ConnectCommand;

/// <summary>
/// Command to skip to next track.
/// </summary>
public sealed record SkipNextCommand : ConnectCommand;

/// <summary>
/// Command to skip to previous track.
/// </summary>
public sealed record SkipPrevCommand : ConnectCommand;
