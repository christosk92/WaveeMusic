using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to set multiple playback options at once (shuffle, repeat).
/// This is a combined options command from Spotify.
/// </summary>
public sealed record SetOptionsCommand : ConnectCommand
{
    /// <summary>
    /// Whether shuffle should be enabled (null if not changing).
    /// </summary>
    public bool? ShufflingContext { get; init; }

    /// <summary>
    /// Whether context repeat should be enabled (null if not changing).
    /// </summary>
    public bool? RepeatingContext { get; init; }

    /// <summary>
    /// Whether track repeat should be enabled (null if not changing).
    /// </summary>
    public bool? RepeatingTrack { get; init; }

    internal static SetOptionsCommand FromJson(DealerRequest request, JsonElement json)
    {
        bool? shuffling = null;
        bool? repeatingContext = null;
        bool? repeatingTrack = null;

        // Options can be in a nested "options" object or at root level
        var options = json.TryGetProperty("options", out var optProp) ? optProp : json;

        if (options.TryGetProperty("shuffling_context", out var shuf))
        {
            shuffling = shuf.ValueKind == JsonValueKind.True;
        }

        if (options.TryGetProperty("repeating_context", out var repCtx))
        {
            repeatingContext = repCtx.ValueKind == JsonValueKind.True;
        }

        if (options.TryGetProperty("repeating_track", out var repTrk))
        {
            repeatingTrack = repTrk.ValueKind == JsonValueKind.True;
        }

        return new SetOptionsCommand
        {
            Endpoint = "set_options",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            ShufflingContext = shuffling,
            RepeatingContext = repeatingContext,
            RepeatingTrack = repeatingTrack
        };
    }
}
