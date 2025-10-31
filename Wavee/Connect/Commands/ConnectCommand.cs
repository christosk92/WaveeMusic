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
    public static ConnectCommand? Parse(DealerRequest request)
    {
        // Extract endpoint from message_ident
        // Format: "hm://connect-state/v1/{endpoint}"
        var parts = request.MessageIdent.Split('/');
        if (parts.Length < 5) return null;  // Need at least 5 parts for hm://connect-state/v1/{endpoint}

        var endpoint = parts[^1];  // Get last element (the endpoint)
        var command = request.Command;

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
