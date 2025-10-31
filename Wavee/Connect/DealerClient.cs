using System.Buffers;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Connection;
using Wavee.Connect.Protocol;
using Wavee.Core.Session;
using Wavee.Core.Utilities;

namespace Wavee.Connect;

/// <summary>
/// High-performance dealer client using IObservable pattern.
/// Provides reactive streams for dealer messages and requests.
/// </summary>
public sealed class DealerClient : IAsyncDisposable
{
    private readonly ILogger? _logger;
    private readonly IDealerConnection _connection;
    private readonly DealerClientConfig _config;

    // Hot observables (SafeSubject<T> isolates subscriber exceptions)
    private readonly SafeSubject<DealerMessage> _messages;
    private readonly SafeSubject<DealerRequest> _requests;
    private readonly BehaviorSubject<Connection.ConnectionState> _connectionState = new(Connection.ConnectionState.Disconnected);
    private readonly BehaviorSubject<string?> _connectionId = new(null);

    // Managers for heartbeat and reconnection
    private HeartbeatManager? _heartbeatManager;
    private ReconnectionManager? _reconnectionManager;

    // AsyncWorker for non-blocking message/request dispatch
    private AsyncWorker<DealerMessage>? _messageWorker;
    private AsyncWorker<DealerRequest>? _requestWorker;

    // Session and HttpClient for reconnection
    private Core.Session.ISession? _session;
    private HttpClient? _httpClient;

    // Cached PONG message (zero allocation on send)
    private static readonly ReadOnlyMemory<byte> PongMessageBytes =
        Encoding.UTF8.GetBytes("{\"type\":\"pong\"}");

    // Cached PING message (zero allocation on send)
    private static readonly ReadOnlyMemory<byte> PingMessageBytes =
        Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");

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
    /// Observable stream of connection ID updates.
    /// </summary>
    /// <remarks>
    /// The connection ID is received from hm://pusher/v1/connections/ messages.
    /// It is required for announcing device presence via SpClient.PutConnectStateAsync().
    /// </remarks>
    public IObservable<string?> ConnectionId => _connectionId.AsObservable();

    /// <summary>
    /// Gets the current connection ID (null if not yet received).
    /// </summary>
    public string? CurrentConnectionId => _connectionId.Value;

    /// <summary>
    /// Initializes a new instance of the <see cref="DealerClient"/> class.
    /// </summary>
    /// <param name="config">Configuration for dealer client behavior.</param>
    /// <param name="connection">Optional dealer connection (for testing). If null, creates a new DealerConnection.</param>
    public DealerClient(DealerClientConfig? config = null, IDealerConnection? connection = null)
    {
        _config = config ?? new DealerClientConfig();
        _logger = _config.Logger;
        _connection = connection ?? new DealerConnection(_config.Logger);

        // Initialize SafeSubjects with logger for exception isolation
        _messages = new SafeSubject<DealerMessage>(_logger);
        _requests = new SafeSubject<DealerRequest>(_logger);

        // Create AsyncWorkers for message and request dispatch
        // SafeSubject handles exception isolation, so no try-catch needed here
        _messageWorker = new AsyncWorker<DealerMessage>(
            "DealerMessages",
            msg =>
            {
                _messages.OnNext(msg);
                return ValueTask.CompletedTask;
            },
            _logger);

        _requestWorker = new AsyncWorker<DealerRequest>(
            "DealerRequests",
            req =>
            {
                _requests.OnNext(req);
                return ValueTask.CompletedTask;
            },
            _logger);

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
        Core.Session.ISession session,
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

                    // Store session and httpClient for reconnection
                    _session = session;
                    _httpClient = httpClient;

                    // Start heartbeat manager
                    InitializeHeartbeat();

                    // Initialize reconnection manager
                    InitializeReconnection();

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
                    // Notify heartbeat manager that PONG was received
                    _heartbeatManager?.RecordPong();
                    _logger?.LogTrace("Received PONG from server");
                    break;

                case Protocol.MessageType.Message:
                    // Log raw message JSON for debugging
                    _logger?.LogTrace("Parsing MESSAGE: size={Size} bytes, json={Json}",
                        rawBytes.Length,
                        System.Text.Encoding.UTF8.GetString(rawBytes.Span));

                    // Parse and dispatch via AsyncWorker
                    if (Protocol.MessageParser.TryParseMessage(rawBytes.Span, out var message) && message != null)
                    {
                        _logger?.LogTrace("Received MESSAGE: uri={Uri}, headerCount={HeaderCount}, payloadSize={PayloadSize}",
                            message.Uri, message.Headers.Count, message.Payload.Length);

                        // Log all headers for pusher messages to debug connection ID
                        if (message.Uri.StartsWith("hm://pusher/", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.LogDebug("PUSHER MESSAGE: {Uri} | Headers: {Headers}",
                                message.Uri,
                                message.Headers.Count > 0 ? string.Join(", ", message.Headers.Select(h => $"{h.Key}={h.Value}")) : "(none)");
                        }

                        // Check for connection ID message
                        if (message.Uri.StartsWith("hm://pusher/v1/connections/", StringComparison.OrdinalIgnoreCase))
                        {
                            HandleConnectionIdMessage(message);
                        }

                        if (_messageWorker != null)
                        {
                            _logger?.LogTrace("Submitting message to worker: uri={Uri}", message.Uri);
                            await _messageWorker.SubmitAsync(message);
                            _logger?.LogTrace("Message submitted to worker successfully");
                        }
                    }
                    else
                    {
                        _logger?.LogWarning("Failed to parse MESSAGE");
                    }
                    break;

                case Protocol.MessageType.Request:
                    // Parse and dispatch via AsyncWorker
                    if (Protocol.MessageParser.TryParseRequest(rawBytes.Span, out var request) && request != null)
                    {
                        _logger?.LogTrace("Received REQUEST: {MessageIdent} (key: {Key})",
                            request.MessageIdent, request.Key);
                        if (_requestWorker != null)
                            await _requestWorker.SubmitAsync(request);
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

        // Stop heartbeat
        _ = StopHeartbeatAsync();

        // Trigger reconnection if enabled
        if (_config.EnableAutoReconnect && !_disposed)
        {
            _reconnectionManager?.TriggerReconnection();
        }
    }

    /// <summary>
    /// Handles connection error event.
    /// </summary>
    private void OnConnectionError(object? sender, Exception ex)
    {
        _logger?.LogError(ex, "Dealer connection error");
        _connectionState.OnNext(Connection.ConnectionState.Disconnected);

        // Stop heartbeat
        _ = StopHeartbeatAsync();

        // Trigger reconnection if enabled
        if (_config.EnableAutoReconnect && !_disposed)
        {
            _reconnectionManager?.TriggerReconnection();
        }
    }

    /// <summary>
    /// Initializes and starts the heartbeat manager.
    /// </summary>
    private void InitializeHeartbeat()
    {
        _heartbeatManager = new HeartbeatManager(
            _config.PingInterval,
            _config.PongTimeout,
            SendPingAsync,
            _logger);

        _heartbeatManager.HeartbeatTimeout += OnHeartbeatTimeout;
        _heartbeatManager.Start();

        _logger?.LogDebug("Heartbeat manager initialized and started");
    }

    /// <summary>
    /// Initializes the reconnection manager.
    /// </summary>
    private void InitializeReconnection()
    {
        _reconnectionManager = new ReconnectionManager(
            _config.InitialReconnectDelay,
            _config.MaxReconnectDelay,
            _config.MaxReconnectAttempts,
            ReconnectInternalAsync,
            _logger);

        _reconnectionManager.ReconnectionSucceeded += OnReconnectionSucceeded;
        _reconnectionManager.ReconnectionFailed += OnReconnectionFailed;

        _logger?.LogDebug("Reconnection manager initialized");
    }

    /// <summary>
    /// Handles heartbeat timeout event.
    /// </summary>
    private void OnHeartbeatTimeout(object? sender, EventArgs e)
    {
        _logger?.LogWarning("Heartbeat timeout detected, triggering reconnection");

        // Close current connection
        _ = _connection.CloseAsync();

        // Trigger reconnection
        if (_config.EnableAutoReconnect && !_disposed)
        {
            _reconnectionManager?.TriggerReconnection();
        }
    }

    /// <summary>
    /// Internal reconnection logic called by ReconnectionManager.
    /// </summary>
    private async ValueTask ReconnectInternalAsync()
    {
        if (_session == null || _httpClient == null)
            throw new InvalidOperationException("Session and HttpClient not available for reconnection");

        _logger?.LogDebug("Attempting reconnection...");

        // Cleanup old connection
        await StopHeartbeatAsync();

        // Attempt to connect
        await ConnectAsync(_session, _httpClient, _cts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Handles successful reconnection.
    /// </summary>
    private void OnReconnectionSucceeded(object? sender, EventArgs e)
    {
        _logger?.LogInformation("Reconnection succeeded");
        _connectionState.OnNext(Connection.ConnectionState.Connected);
    }

    /// <summary>
    /// Handles failed reconnection (all attempts exhausted).
    /// </summary>
    private void OnReconnectionFailed(object? sender, EventArgs e)
    {
        _logger?.LogError("Reconnection failed after all attempts exhausted");
        _connectionState.OnNext(Connection.ConnectionState.Disconnected);
    }

    /// <summary>
    /// Stops the heartbeat manager.
    /// </summary>
    private async ValueTask StopHeartbeatAsync()
    {
        if (_heartbeatManager != null)
        {
            await _heartbeatManager.StopAsync();
            _heartbeatManager.HeartbeatTimeout -= OnHeartbeatTimeout;
            await _heartbeatManager.DisposeAsync();
            _heartbeatManager = null;
        }
    }

    /// <summary>
    /// Sends a PING message to the server.
    /// Uses cached message bytes for zero allocation.
    /// </summary>
    private ValueTask SendPingAsync()
    {
        return _connection.SendAsync(PingMessageBytes);
    }

    /// <summary>
    /// Handles connection ID messages from hm://pusher/v1/connections/.
    /// Extracts and stores the Spotify-Connection-Id header.
    /// </summary>
    private void HandleConnectionIdMessage(DealerMessage message)
    {
        _logger?.LogTrace("HandleConnectionIdMessage called: uri={Uri}, headerCount={HeaderCount}",
            message.Uri, message.Headers.Count);

        _logger?.LogTrace("Looking for Spotify-Connection-Id header. Available headers: {Headers}",
            string.Join(", ", message.Headers.Keys));

        if (message.Headers.TryGetValue("Spotify-Connection-Id", out var connectionId))
        {
            _logger?.LogInformation("Received connection ID: {ConnectionId}", connectionId);
            _logger?.LogTrace("Emitting connection ID to ConnectionId observable: {ConnectionId}", connectionId);
            _connectionId.OnNext(connectionId);
            _logger?.LogTrace("Connection ID observable OnNext completed successfully");
        }
        else
        {
            _logger?.LogWarning("Connection message received but Spotify-Connection-Id header not found. Available headers: {Headers}",
                string.Join(", ", message.Headers.Keys));
        }
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

        // Stop heartbeat and reconnection
        await StopHeartbeatAsync();

        if (_reconnectionManager != null)
        {
            await _reconnectionManager.CancelReconnectionAsync();
            _reconnectionManager.ReconnectionSucceeded -= OnReconnectionSucceeded;
            _reconnectionManager.ReconnectionFailed -= OnReconnectionFailed;
            await _reconnectionManager.DisposeAsync();
            _reconnectionManager = null;
        }

        // Complete and dispose workers
        if (_messageWorker != null)
        {
            await _messageWorker.CompleteAsync();
            await _messageWorker.DisposeAsync();
            _messageWorker = null;
        }

        if (_requestWorker != null)
        {
            await _requestWorker.CompleteAsync();
            await _requestWorker.DisposeAsync();
            _requestWorker = null;
        }

        // Stop observables
        _messages.OnCompleted();
        _requests.OnCompleted();
        _connectionState.OnCompleted();
        _connectionId.OnCompleted();

        // Cleanup
        _cts?.Cancel();
        _cts?.Dispose();

        await _connection.DisposeAsync();

        // Dispose subjects
        _messages.Dispose();
        _requests.Dispose();
        _connectionState.Dispose();
        _connectionId.Dispose();

        _logger?.LogDebug("DealerClient disposed");
    }
}
