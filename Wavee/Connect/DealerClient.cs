using System.Buffers;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Connection;
using Wavee.Connect.Protocol;
using Wavee.Core.Session;

namespace Wavee.Connect;

/// <summary>
/// High-performance dealer client using IObservable pattern.
/// Provides reactive streams for dealer messages and requests.
/// </summary>
public sealed class DealerClient : IAsyncDisposable
{
    private readonly ILogger<DealerClient>? _logger;
    private readonly DealerConnection _connection;

    // Hot observables (Subject<T> pushes to subscribers)
    private readonly Subject<DealerMessage> _messages = new();
    private readonly Subject<DealerRequest> _requests = new();
    private readonly BehaviorSubject<Connection.ConnectionState> _connectionState = new(Connection.ConnectionState.Disconnected);

    // Cached PONG message (zero allocation on send)
    private static readonly ReadOnlyMemory<byte> PongMessageBytes =
        Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");

    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    /// Observable stream of all dealer messages.
    /// Use .Where() to filter by URI prefix.
    /// </summary>
    public IObservable<DealerMessage> Messages => _messages.AsObservable();

    /// <summary>
    /// Observable stream of all dealer requests.
    /// Use .Where() to filter by message_ident prefix.
    /// Requests should be replied to using SendReplyAsync().
    /// </summary>
    public IObservable<DealerRequest> Requests => _requests.AsObservable();

    /// <summary>
    /// Observable stream of connection state changes.
    /// </summary>
    public IObservable<ConnectionState> ConnectionState => _connectionState.AsObservable();

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public ConnectionState CurrentState => _connectionState.Value;

    /// <summary>
    /// Initializes a new instance of the <see cref="DealerClient"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public DealerClient(ILogger<DealerClient>? logger = null)
    {
        _logger = logger;
        _connection = new DealerConnection(logger as ILogger<DealerConnection>);

        // Subscribe to connection events
        _connection.MessageReceived += OnRawMessageReceivedAsync;
        _connection.Closed += OnConnectionClosed;
        _connection.Error += OnConnectionError;
    }

    /// <summary>
    /// Connects to the dealer WebSocket endpoint.
    /// </summary>
    /// <param name="session">Active Spotify session for token/endpoint resolution.</param>
    /// <param name="httpClient">HTTP client for ApResolver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="DealerException">Thrown if connection fails.</exception>
    public async ValueTask ConnectAsync(
        Core.Session.Session session,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DealerClient));

        if (_connectionState.Value == Connection.ConnectionState.Connected)
            throw new InvalidOperationException("Already connected");

        _connectionState.OnNext(Connection.ConnectionState.Connecting);
        _cts = new CancellationTokenSource();

        try
        {
            // Get access token from session
            _logger?.LogDebug("Fetching access token from session");
            var accessToken = await session.GetAccessTokenAsync(cancellationToken);

            // Resolve dealer endpoints
            _logger?.LogDebug("Resolving dealer endpoints");
            var dealers = await ApResolver.ResolveDealerAsync(httpClient, _logger, cancellationToken);

            if (dealers.Count == 0)
                throw new DealerException(
                    DealerFailureReason.ResolveFailed,
                    "No dealer endpoints available");

            // Try each dealer endpoint until one connects
            Exception? lastException = null;
            foreach (var dealer in dealers)
            {
                try
                {
                    var wsUrl = $"wss://{dealer}/?access_token={accessToken.Token}";
                    _logger?.LogDebug("Attempting connection to dealer: {Dealer}", dealer);

                    await _connection.ConnectAsync(wsUrl, cancellationToken);

                    _connectionState.OnNext(Connection.ConnectionState.Connected);
                    _logger?.LogInformation("Connected to dealer: {Dealer}", dealer);
                    return;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to connect to dealer: {Dealer}", dealer);
                    lastException = ex;
                }
            }

            // All dealers failed
            _connectionState.OnNext(Connection.ConnectionState.Disconnected);
            throw new DealerException(
                DealerFailureReason.ConnectionFailed,
                "Failed to connect to any dealer endpoint",
                lastException);
        }
        catch (Exception ex) when (ex is not DealerException)
        {
            _connectionState.OnNext(Connection.ConnectionState.Disconnected);
            throw new DealerException(
                DealerFailureReason.ConnectionFailed,
                "Failed to connect to dealer",
                ex);
        }
    }

    /// <summary>
    /// Sends a reply to a dealer request.
    /// </summary>
    /// <param name="key">Request key from DealerRequest.Key.</param>
    /// <param name="result">Result of request handling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask SendReplyAsync(
        string key,
        RequestResult result,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DealerClient));

        // Build reply JSON: {"type":"reply","key":"...","payload":{"success":true}}
        var success = result == RequestResult.Success;
        var replyJson = $"{{\"type\":\"reply\",\"key\":\"{key}\",\"payload\":{{\"success\":{success.ToString().ToLowerInvariant()}}}}}";

        await _connection.SendAsync(replyJson, cancellationToken);
        _logger?.LogTrace("Sent reply for key {Key}: {Result}", key, result);
    }

    /// <summary>
    /// Disconnects from the dealer WebSocket.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connectionState.Value == Connection.ConnectionState.Disconnected)
            return;

        _logger?.LogDebug("Disconnecting from dealer");

        _cts?.Cancel();
        await _connection.CloseAsync(cancellationToken);

        _connectionState.OnNext(Connection.ConnectionState.Disconnected);
        _logger?.LogInformation("Disconnected from dealer");
    }

    /// <summary>
    /// Handles raw WebSocket messages from DealerConnection.
    /// Parses and routes to appropriate observable streams.
    /// </summary>
    private async ValueTask OnRawMessageReceivedAsync(ReadOnlyMemory<byte> rawBytes)
    {
        if (_disposed || _cts?.IsCancellationRequested == true)
            return;

        try
        {
            // Parse message type
            var messageType = Protocol.MessageParser.ParseMessageType(rawBytes.Span);

            switch (messageType)
            {
                case Protocol.MessageType.Ping:
                    // Automatic PONG response
                    await SendPongAsync();
                    _logger?.LogTrace("Received PING, sent PONG");
                    break;

                case Protocol.MessageType.Pong:
                    // Server acknowledged our PONG (keep-alive confirmation)
                    _logger?.LogTrace("Received PONG from server");
                    break;

                case Protocol.MessageType.Message:
                    // Parse and push to Messages observable
                    if (Protocol.MessageParser.TryParseMessage(rawBytes.Span, out var message))
                    {
                        _logger?.LogTrace("Received MESSAGE: {Uri}", message.Uri);
                        _messages.OnNext(message);
                    }
                    else
                    {
                        _logger?.LogWarning("Failed to parse MESSAGE");
                    }
                    break;

                case Protocol.MessageType.Request:
                    // Parse and push to Requests observable
                    if (Protocol.MessageParser.TryParseRequest(rawBytes.Span, out var request))
                    {
                        _logger?.LogTrace("Received REQUEST: {MessageIdent} (key: {Key})",
                            request.MessageIdent, request.Key);
                        _requests.OnNext(request);
                    }
                    else
                    {
                        _logger?.LogWarning("Failed to parse REQUEST");
                    }
                    break;

                case Protocol.MessageType.Unknown:
                    _logger?.LogWarning("Received unknown message type");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing dealer message");
        }
    }

    /// <summary>
    /// Sends a PONG message in response to PING.
    /// Uses cached message bytes for zero allocation.
    /// </summary>
    private ValueTask SendPongAsync()
    {
        return _connection.SendAsync(PongMessageBytes);
    }

    /// <summary>
    /// Handles connection closed event.
    /// </summary>
    private void OnConnectionClosed(object? sender, System.Net.WebSockets.WebSocketCloseStatus? closeStatus)
    {
        _logger?.LogInformation("Dealer connection closed: {Status}", closeStatus);
        _connectionState.OnNext(Connection.ConnectionState.Disconnected);
    }

    /// <summary>
    /// Handles connection error event.
    /// </summary>
    private void OnConnectionError(object? sender, Exception ex)
    {
        _logger?.LogError(ex, "Dealer connection error");
        _connectionState.OnNext(Connection.ConnectionState.Disconnected);
    }

    /// <summary>
    /// Disposes the dealer client and releases all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger?.LogDebug("Disposing DealerClient");

        // Stop observables
        _messages.OnCompleted();
        _requests.OnCompleted();
        _connectionState.OnCompleted();

        // Cleanup
        _cts?.Cancel();
        _cts?.Dispose();

        await _connection.DisposeAsync();

        // Dispose subjects
        _messages.Dispose();
        _requests.Dispose();
        _connectionState.Dispose();

        _logger?.LogDebug("DealerClient disposed");
    }
}
