using System.Text.Json;
using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Command to set the playback queue.
/// </summary>
public sealed record SetQueueCommand : ConnectCommand
{
    /// <summary>
    /// URIs of tracks to set as queue.
    /// </summary>
    public required string[] TrackUris { get; init; }

    internal static SetQueueCommand FromJson(DealerRequest request, JsonElement json)
    {
        var nextTracks = json.GetProperty("next_tracks");
        var trackUris = nextTracks.EnumerateArray()
            .Select(t => t.GetProperty("uri").GetString()!)
            .ToArray();

        return new SetQueueCommand
        {
            Endpoint = "set_queue",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            TrackUris = trackUris
        };
    }
}

/// <summary>
/// Command to add track(s) to playback queue.
/// </summary>
public sealed record AddToQueueCommand : ConnectCommand
{
    /// <summary>
    /// URI of track to add to queue.
    /// </summary>
    public required string TrackUri { get; init; }

    internal static AddToQueueCommand FromJson(DealerRequest request, JsonElement json)
    {
        var trackUri = json.GetProperty("track_uri").GetString()!;

        return new AddToQueueCommand
        {
            Endpoint = "add_to_queue",
            MessageIdent = request.MessageIdent,
            MessageId = request.MessageId,
            SenderDeviceId = request.SenderDeviceId,
            Key = request.Key,
            TrackUri = trackUri
        };
    }
}
