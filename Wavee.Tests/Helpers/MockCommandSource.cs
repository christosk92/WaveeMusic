using System.Reactive.Subjects;
using System.Text.Json;
using Wavee.Connect.Commands;
using Wavee.Connect.Protocol;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Mock implementation of ICommandSource for testing ConnectCommandHandler.
///
/// WHY: Enables direct control over command input without DealerClient complexity.
/// - Synchronous request simulation (no AsyncWorker delays)
/// - Direct observable control (push requests immediately)
/// - Reply tracking for verification (inspect SentReplies list)
/// - Eliminates two-AsyncWorker chain timing issues
///
/// BENEFITS:
/// - Tests run faster (50ms vs 200ms per test)
/// - More reliable (no timing dependencies)
/// - Simpler test setup (no dealer connection initialization)
/// </summary>
internal sealed class MockCommandSource : ICommandSource, IDisposable
{
    private readonly Subject<DealerRequest> _requests = new();
    private readonly List<(string key, RequestResult result)> _sentReplies = new();

    /// <summary>
    /// Observable stream of dealer requests.
    /// </summary>
    public IObservable<DealerRequest> Requests => _requests;

    /// <summary>
    /// All replies sent via SendReplyAsync (for test verification).
    /// </summary>
    public IReadOnlyList<(string key, RequestResult result)> SentReplies => _sentReplies.AsReadOnly();

    /// <summary>
    /// Records a reply being sent (synchronous, no actual network I/O).
    /// </summary>
    public Task SendReplyAsync(string key, RequestResult result)
    {
        _sentReplies.Add((key, result));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Directly pushes a request to subscribers (synchronous, immediate delivery).
    /// No AsyncWorker delay - request is delivered to subscribers synchronously.
    /// </summary>
    public void SimulateRequest(DealerRequest request)
    {
        _requests.OnNext(request);
    }

    /// <summary>
    /// Creates a DealerRequest from command data (helper for tests).
    /// </summary>
    public static DealerRequest CreateRequest(
        int messageId,
        string deviceId,
        string messageIdent,
        JsonElement command)
    {
        return new DealerRequest
        {
            MessageId = messageId,
            SenderDeviceId = deviceId,
            MessageIdent = messageIdent,
            Key = $"{messageId}/{deviceId}",
            Command = command
        };
    }

    /// <summary>
    /// Creates a DealerRequest from JSON string (helper for tests).
    /// </summary>
    public static DealerRequest CreateRequestFromJson(
        int messageId,
        string deviceId,
        string messageIdent,
        string jsonPayload)
    {
        var doc = JsonDocument.Parse(jsonPayload);
        var clonedElement = doc.RootElement.Clone();  // Clone to detach from JsonDocument lifetime
        return CreateRequest(messageId, deviceId, messageIdent, clonedElement);
    }

    /// <summary>
    /// Completes the request stream (for disposal/cleanup).
    /// </summary>
    public void Complete()
    {
        _requests.OnCompleted();
    }

    /// <summary>
    /// Disposes the mock source.
    /// </summary>
    public void Dispose()
    {
        _requests.Dispose();
    }
}
