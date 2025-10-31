using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Abstraction for command input/output used by ConnectCommandHandler.
///
/// WHY: Enables dependency injection for better testability.
/// - Production code uses DealerClientCommandSource adapter
/// - Test code uses MockCommandSource for direct control
/// - Decouples ConnectCommandHandler from DealerClient implementation details
///
/// PATTERN: Adapter/Strategy pattern for dependency inversion
/// </summary>
public interface ICommandSource
{
    /// <summary>
    /// Observable stream of incoming dealer requests.
    /// ConnectCommandHandler subscribes to this and filters for Connect commands.
    /// </summary>
    IObservable<DealerRequest> Requests { get; }

    /// <summary>
    /// Sends a reply for a command request back to Spotify.
    /// </summary>
    /// <param name="key">The request key (format: "messageId/deviceId")</param>
    /// <param name="result">The command execution result</param>
    Task SendReplyAsync(string key, RequestResult result);
}
