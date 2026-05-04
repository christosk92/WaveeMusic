using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to set shuffle mode.
/// </summary>
public sealed record ShuffleCommand : ConnectCommand
{
    /// <summary>
    /// Whether shuffle should be enabled.
    /// </summary>
    public required bool Enabled { get; init; }

    internal static ShuffleCommand FromJson(DealerRequest request, JsonElement json)
    {
        var enabled = json.GetProperty("value").GetBoolean();

        return new ShuffleCommand
        {
            Endpoint = "set_shuffling_context",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            Enabled = enabled
        };
    }
}

/// <summary>
/// Command to set context repeat mode.
/// </summary>
public sealed record RepeatContextCommand : ConnectCommand
{
    /// <summary>
    /// Whether context repeat should be enabled.
    /// </summary>
    public required bool Enabled { get; init; }

    internal static RepeatContextCommand FromJson(DealerRequest request, JsonElement json)
    {
        var enabled = json.GetProperty("value").GetBoolean();

        return new RepeatContextCommand
        {
            Endpoint = "set_repeating_context",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            Enabled = enabled
        };
    }
}

/// <summary>
/// Command to set track repeat mode.
/// </summary>
public sealed record RepeatTrackCommand : ConnectCommand
{
    /// <summary>
    /// Whether track repeat should be enabled.
    /// </summary>
    public required bool Enabled { get; init; }

    internal static RepeatTrackCommand FromJson(DealerRequest request, JsonElement json)
    {
        var enabled = json.GetProperty("value").GetBoolean();

        return new RepeatTrackCommand
        {
            Endpoint = "set_repeating_track",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            Enabled = enabled
        };
    }
}
