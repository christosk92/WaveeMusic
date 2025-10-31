using Wavee.Connect.Protocol;

namespace Wavee.Connect.Commands;

/// <summary>
/// Adapter that implements ICommandSource using DealerClient.
///
/// WHY: Maintains existing production behavior while enabling DI pattern.
/// - Wraps DealerClient to provide ICommandSource interface
/// - Used by primary ConnectCommandHandler constructor for backwards compatibility
/// - Zero overhead - direct delegation to DealerClient methods
///
/// PATTERN: Adapter pattern - adapts DealerClient to ICommandSource interface
/// </summary>
internal sealed class DealerClientCommandSource : ICommandSource
{
    private readonly DealerClient _dealerClient;

    /// <summary>
    /// Creates a command source from a DealerClient.
    /// </summary>
    /// <param name="dealerClient">The dealer client to wrap</param>
    public DealerClientCommandSource(DealerClient dealerClient)
    {
        _dealerClient = dealerClient ?? throw new ArgumentNullException(nameof(dealerClient));
    }

    /// <summary>
    /// Gets the dealer request observable from the client.
    /// </summary>
    public IObservable<DealerRequest> Requests => _dealerClient.Requests;

    /// <summary>
    /// Sends a reply via the dealer client.
    /// </summary>
    public Task SendReplyAsync(string key, RequestResult result)
    {
        return _dealerClient.SendReplyAsync(key, result).AsTask();
    }
}
