using System.Text.Json;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Protocol;
using Wavee.Tests.Connect;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Test helpers for ConnectCommandHandler tests.
/// Provides utilities for creating test dealer requests and simulating commands.
///
/// UPDATED: Now uses MockCommandSource for better testability.
/// - No DealerClient initialization required
/// - Synchronous request simulation
/// - Direct control over command flow
/// </summary>
internal static class ConnectCommandTestHelpers
{
    /// <summary>
    /// Creates a ConnectCommandHandler with MockCommandSource for testing.
    /// Returns the handler and mock source (no async initialization required).
    /// </summary>
    public static (ConnectCommandHandler handler, MockCommandSource source)
        CreateTestCommandHandler()
    {
        var mockSource = new MockCommandSource();
        var handler = new ConnectCommandHandler(mockSource);

        return (handler, mockSource);
    }

    /// <summary>
    /// Creates a dealer request for a play command.
    /// </summary>
    public static DealerRequest CreatePlayCommandRequest(
        int messageId,
        string deviceId,
        string? contextUri = null,
        string? trackUri = null,
        long? seekTo = null,
        int? skipToIndex = null,
        bool? shuffling = null,
        bool? repeatingContext = null,
        bool? repeatingTrack = null)
    {
        var command = new Dictionary<string, object?>();

        if (contextUri != null)
            command["context_uri"] = contextUri;

        if (trackUri != null)
            command["track"] = new { uri = trackUri };

        if (seekTo.HasValue)
            command["seek_to"] = seekTo.Value;

        if (skipToIndex.HasValue)
            command["skip_to"] = new { track_index = skipToIndex.Value };

        if (shuffling.HasValue || repeatingContext.HasValue || repeatingTrack.HasValue)
        {
            var options = new Dictionary<string, bool>();
            if (shuffling.HasValue) options["shuffling_context"] = shuffling.Value;
            if (repeatingContext.HasValue) options["repeating_context"] = repeatingContext.Value;
            if (repeatingTrack.HasValue) options["repeating_track"] = repeatingTrack.Value;
            command["options"] = options;
        }

        var json = JsonSerializer.Serialize(command);
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/play",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for a pause command.
    /// </summary>
    public static DealerRequest CreatePauseCommandRequest(int messageId, string deviceId)
    {
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/pause",
            jsonPayload: "{}");
    }

    /// <summary>
    /// Creates a dealer request for a resume command.
    /// </summary>
    public static DealerRequest CreateResumeCommandRequest(int messageId, string deviceId)
    {
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/resume",
            jsonPayload: "{}");
    }

    /// <summary>
    /// Creates a dealer request for a seek command.
    /// </summary>
    public static DealerRequest CreateSeekCommandRequest(int messageId, string deviceId, long positionMs)
    {
        var json = JsonSerializer.Serialize(new { position = positionMs });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/seek_to",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for a skip next command.
    /// </summary>
    public static DealerRequest CreateSkipNextCommandRequest(int messageId, string deviceId)
    {
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/skip_next",
            jsonPayload: "{}");
    }

    /// <summary>
    /// Creates a dealer request for a skip previous command.
    /// </summary>
    public static DealerRequest CreateSkipPrevCommandRequest(int messageId, string deviceId)
    {
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/skip_prev",
            jsonPayload: "{}");
    }

    /// <summary>
    /// Creates a dealer request for a shuffle command.
    /// </summary>
    public static DealerRequest CreateShuffleCommandRequest(int messageId, string deviceId, bool enabled)
    {
        var json = JsonSerializer.Serialize(new { value = enabled });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/set_shuffling_context",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for a repeat context command.
    /// </summary>
    public static DealerRequest CreateRepeatContextCommandRequest(int messageId, string deviceId, bool enabled)
    {
        var json = JsonSerializer.Serialize(new { value = enabled });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/set_repeating_context",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for a repeat track command.
    /// </summary>
    public static DealerRequest CreateRepeatTrackCommandRequest(int messageId, string deviceId, bool enabled)
    {
        var json = JsonSerializer.Serialize(new { value = enabled });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/set_repeating_track",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for a transfer command.
    /// </summary>
    public static DealerRequest CreateTransferCommandRequest(int messageId, string deviceId, byte[] transferStateBytes)
    {
        var transferStateB64 = Convert.ToBase64String(transferStateBytes);
        var json = JsonSerializer.Serialize(new { transfer_state = transferStateB64 });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/transfer",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for a set queue command.
    /// </summary>
    public static DealerRequest CreateSetQueueCommandRequest(int messageId, string deviceId, string[] trackUris)
    {
        var nextTracks = trackUris.Select(uri => new { uri }).ToArray();
        var json = JsonSerializer.Serialize(new { next_tracks = nextTracks });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/set_queue",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for an add to queue command.
    /// </summary>
    public static DealerRequest CreateAddToQueueCommandRequest(int messageId, string deviceId, string trackUri)
    {
        var json = JsonSerializer.Serialize(new { track_uri = trackUri });
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: "hm://connect-state/v1/add_to_queue",
            jsonPayload: json);
    }

    /// <summary>
    /// Creates a dealer request for an unknown/unsupported command.
    /// </summary>
    public static DealerRequest CreateUnknownCommandRequest(int messageId, string deviceId, string endpoint)
    {
        return MockCommandSource.CreateRequestFromJson(
            messageId: messageId,
            deviceId: deviceId,
            messageIdent: $"hm://connect-state/v1/{endpoint}",
            jsonPayload: "{}");
    }

    /// <summary>
    /// Simulates sending a command through the mock command source.
    /// This is synchronous - the request is pushed directly to subscribers.
    /// </summary>
    public static void SimulateCommand(MockCommandSource source, DealerRequest request)
    {
        source.SimulateRequest(request);
    }

    /// <summary>
    /// Waits for async operations to complete (used after sending commands).
    /// Increased to 500ms to ensure AsyncWorker has time to process.
    /// </summary>
    public static Task WaitForProcessingAsync(int delayMs = 500)
    {
        return Task.Delay(delayMs);
    }
}
