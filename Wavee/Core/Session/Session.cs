using System.Net.Sockets;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Extensions.Logging;
using Wavee.Core.Authentication;
using Wavee.Core.Connection;
using Wavee.Core.Http;

namespace Wavee.Core.Session;

/// <summary>
/// Represents an active Spotify session with connection and authentication.
/// </summary>
/// <remarks>
/// Lifecycle:
/// 1. Create session with SessionConfig
/// 2. Call ConnectAsync() to establish connection and authenticate
/// 3. Use SendAsync() / packet events for communication
/// 4. Call DisposeAsync() to cleanup
///
/// The session automatically manages:
/// - Access Point connection with retry
/// - Keep-alive ping/pong protocol
/// - Background packet dispatcher
/// - Lazy initialization of subsystems
/// </remarks>
public sealed class Session : IAsyncDisposable
{
    private readonly SessionConfig _config;
    private readonly SessionData _data;
    private readonly ILogger? _logger;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Packet dispatcher
    private readonly Channel<(byte command, byte[] payload)> _sendQueue;
    private Task? _dispatchTask;
    private CancellationTokenSource? _dispatchCts;

    /// <summary>
    /// Raised when a packet is received from the server.
    /// </summary>
    public event EventHandler<PacketReceivedEventArgs>? PacketReceived;

    /// <summary>
    /// Raised when the session is disconnected.
    /// </summary>
    public event EventHandler? Disconnected;

    private Session(SessionConfig config, ILogger? logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient();
        _data = new SessionData(config, _httpClient, logger);

        // Bounded channel for send queue (backpressure)
        _sendQueue = Channel.CreateBounded<(byte, byte[])>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>
    /// Creates a new session with the specified configuration.
    /// </summary>
    /// <param name="config">Session configuration.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <returns>A new session instance.</returns>
    public static Session Create(SessionConfig config, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Session(config, logger);
    }

    /// <summary>
    /// Connects to Spotify and authenticates.
    /// </summary>
    /// <param name="credentials">Authentication credentials.</param>
    /// <param name="credentialsCache">Optional credentials cache for storing reusable credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SessionException">Thrown if connection or authentication fails.</exception>
    public async Task ConnectAsync(
        Credentials credentials,
        ICredentialsCache? credentialsCache = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (_data.IsConnected())
            {
                _logger?.LogWarning("Already connected, disconnecting first");
                await DisconnectInternalAsync();
            }

            _logger?.LogInformation("Connecting to Spotify");

            // 1. Resolve Access Points
            var aps = await ApResolver.ResolveAsync(_httpClient, _logger, cancellationToken);

            // 2. Try each AP until one succeeds
            ApTransport? transport = null;
            string? connectedAp = null;
            Exception? lastException = null;

            foreach (var ap in aps)
            {
                try
                {
                    _logger?.LogDebug("Trying Access Point: {Ap}", ap);
                    transport = await ConnectToApAsync(ap, cancellationToken);
                    connectedAp = ap;
                    _logger?.LogInformation("Connected to Access Point: {Ap}", ap);
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to connect to {Ap}", ap);
                    lastException = ex;
                }
            }

            if (transport is null || connectedAp is null)
            {
                throw new SessionException(
                    SessionFailureReason.ConnectionFailed,
                    "Failed to connect to any Access Point",
                    lastException);
            }

            // 3. Authenticate
            _logger?.LogDebug("Authenticating");
            var reusableCredentials = await Authenticator.AuthenticateAsync(
                transport,
                credentials,
                _config.DeviceId,
                _logger,
                cancellationToken);

            // 4. Store user data
            var userData = new UserData
            {
                Username = reusableCredentials.Username!,
                CountryCode = null,  // Will be populated when packet 0x1b arrives
                AccountType = null   // Will be populated when packet 0x50 arrives
            };

            _data.SetUserData(userData);
            _data.SetTransport(transport, connectedAp);

            // 5. Save stored credentials
            _data.SetStoredCredentials(reusableCredentials);
            if (credentialsCache != null)
            {
                await credentialsCache.SaveCredentialsAsync(reusableCredentials, cancellationToken);
                _logger?.LogInformation("Stored credentials saved to cache");
            }

            // 6. Start packet dispatcher
            _dispatchCts = new CancellationTokenSource();
            _dispatchTask = Task.Run(() => DispatchLoop(_dispatchCts.Token), _dispatchCts.Token);

            _logger?.LogInformation("Session established for user: {Username}", userData.Username);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Sends a packet to the Spotify server.
    /// </summary>
    /// <param name="packetType">The packet type.</param>
    /// <param name="payload">The payload data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="SessionException">Thrown if the session is not connected.</exception>
    public async ValueTask SendAsync(
        PacketType packetType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (!_data.IsConnected())
            throw new SessionException(SessionFailureReason.Disposed, "Session not connected");

        // Queue packet for background sender
        await _sendQueue.Writer.WriteAsync(((byte)packetType, payload.ToArray()), cancellationToken);
    }

    /// <summary>
    /// Gets current user data (or null if not authenticated).
    /// </summary>
    /// <returns>User data if authenticated, otherwise null.</returns>
    public UserData? GetUserData() => _data.GetUserData();

    /// <summary>
    /// Checks if the session is connected and authenticated.
    /// </summary>
    /// <returns>True if connected and authenticated, otherwise false.</returns>
    public bool IsConnected() => _data.IsConnected();

    /// <summary>
    /// Gets the SpClient for making authenticated metadata requests.
    /// </summary>
    /// <remarks>
    /// SpClient automatically obtains and refreshes access tokens using login5.
    /// </remarks>
    /// <returns>SpClient instance.</returns>
    public SpClient SpClient => new SpClient(this, _httpClient, _logger);

    /// <summary>
    /// Waits for the country code to be received from the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The country code (ISO 3166-1 alpha-2).</returns>
    public Task<string> GetCountryCodeAsync(CancellationToken cancellationToken = default)
    {
        return _data.GetCountryCodeAsync().WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for the account type to be received from the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The account type.</returns>
    public Task<AccountType> GetAccountTypeAsync(CancellationToken cancellationToken = default)
    {
        return _data.GetAccountTypeAsync().WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a valid access token for SpClient requests, refreshing if necessary.
    /// </summary>
    /// <remarks>
    /// This method automatically refreshes expired tokens using login5.
    /// Requires that the session is connected and has stored credentials from authentication.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid access token.</returns>
    /// <exception cref="SessionException">Thrown if session is not authenticated or credentials are missing.</exception>
    /// <exception cref="Login5Exception">Thrown if token refresh fails.</exception>
    public async Task<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var token = _data.GetAccessToken();

        // Check if we need to refresh
        if (token == null || token.ShouldRefresh())
        {
            _logger?.LogDebug("Access token expired or missing, refreshing via login5");

            var userData = _data.GetUserData();
            if (userData == null)
                throw new SessionException(
                    SessionFailureReason.Disposed,
                    "Session not authenticated");

            // Get stored credentials from session
            var storedCredentials = _data.GetStoredCredentials();
            if (storedCredentials == null)
                throw new SessionException(
                    SessionFailureReason.Disposed,
                    "No stored credentials available");

            // Exchange for access token via login5
            var login5 = _data.GetLogin5Client();
            token = await login5.GetAccessTokenAsync(
                storedCredentials.Username!,
                storedCredentials.AuthData,
                clientToken: null, // TODO: Add ClientToken manager when implementing SpClient
                cancellationToken);

            _data.SetAccessToken(token);
            _logger?.LogInformation("Access token refreshed (expires {ExpiresAt})", token.ExpiresAt);
        }

        return token;
    }

    private async Task<ApTransport> ConnectToApAsync(
        string apUrl,
        CancellationToken cancellationToken)
    {
        // Parse AP URL (format: "host:port")
        var parts = apUrl.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            throw new SessionException(
                SessionFailureReason.ConnectionFailed,
                $"Invalid AP URL format: {apUrl}");
        }

        var host = parts[0];

        // Connect TCP socket
        var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(host, port, cancellationToken);
        var stream = tcpClient.GetStream();

        // Perform handshake
        try
        {
            return await Handshake.PerformHandshakeAsync(stream, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync();
            tcpClient.Dispose();
            throw new SessionException(
                SessionFailureReason.HandshakeFailed,
                $"Handshake failed with {apUrl}",
                ex);
        }
    }

    private async Task DispatchLoop(CancellationToken cancellationToken)
    {
        var keepAlive = new KeepAlive(_logger);

        try
        {
            _logger?.LogDebug("Packet dispatcher started");

            while (!cancellationToken.IsCancellationRequested)
            {
                var transport = _data.GetTransport();
                if (transport is null)
                {
                    _logger?.LogWarning("Transport became null, stopping dispatcher");
                    break;
                }

            // Handle keep-alive (timeouts only; no proactive Ping)
            var (lastPing, lastPong, missedPongs) = _data.GetKeepAliveState();
            var action = keepAlive.GetNextAction(lastPing, lastPong, missedPongs);

            switch (action)
            {
                case KeepAliveAction.IncrementMissedPong:
                    _data.RecordMissedPong();
                    break;

                case KeepAliveAction.Disconnect:
                    _logger?.LogError("Keep-alive failed, disconnecting");
                    await DisconnectInternalAsync();
                    OnDisconnected();
                    return;
            }

                // Process send queue (non-blocking peek)
                while (_sendQueue.Reader.TryRead(out var item))
                {
                    var (cmd, payload) = item;
                    await transport.SendAsync(cmd, payload, cancellationToken);
                }

                // Receive packets (with timeout)
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    var packet = await transport.ReceiveAsync(linkedCts.Token);
                    if (packet is null)
                    {
                        _logger?.LogInformation("Connection closed by server");
                        await DisconnectInternalAsync();
                        OnDisconnected();
                        return;
                    }

                    var (cmd, payload) = packet.Value;
                    HandlePacket((PacketType)cmd, payload);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // Receive timeout, continue loop
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Packet dispatcher failed");
            await DisconnectInternalAsync();
            OnDisconnected();
        }
        finally
        {
            _logger?.LogDebug("Packet dispatcher stopped");
        }
    }

    private void HandlePacket(PacketType packetType, byte[] payload)
    {
        _logger?.LogTrace("Received packet: {PacketType} ({Size} bytes)", packetType, payload.Length);

        // Handle system packets
        switch (packetType)
        {
            case PacketType.Ping:
                // Respond to server Ping with Pong (4 zero bytes)
                // Queue immediately; dispatcher will send on next loop iteration
                _sendQueue.Writer.TryWrite(((byte)PacketType.Pong, new byte[] { 0x00, 0x00, 0x00, 0x00 }));
                _data.RecordPingSent();
                _logger?.LogTrace("Received Ping from server, queued Pong response");
                return;

            case PacketType.PongAck:
                // Server acknowledges our Pong; treat as successful keep-alive
                _data.RecordPongReceived();
                _logger?.LogTrace("Received PongAck from server (keep-alive confirmed)");
                return;

            case PacketType.Pong:
                _data.RecordPongReceived();
                _logger?.LogTrace("Received Pong from server");
                return;

            case PacketType.CountryCode:
                if (payload.Length >= 2)
                {
                    var countryCode = System.Text.Encoding.ASCII.GetString(payload.AsSpan(0, 2));
                    _data.SetCountryCode(countryCode);
                    _logger?.LogDebug("Received country code: {CountryCode}", countryCode);
                }
                return;

            case PacketType.ProductInfo:
                // ProductInfo packet contains XML with user account information
                try
                {
                    var xml = System.Text.Encoding.UTF8.GetString(payload);
                    _logger?.LogTrace("Received ProductInfo XML: {Xml}", xml);

                    var attributes = ParseProductInfoXml(xml);

                    // Extract and set account type
                    if (attributes.TryGetValue("type", out var accountTypeStr))
                    {
                        var accountType = accountTypeStr.ToLowerInvariant() switch
                        {
                            "premium" => AccountType.Premium,
                            "free" => AccountType.Free,
                            "family" => AccountType.Family,
                            "artist" => AccountType.Artist,
                            _ => AccountType.Unknown
                        };
                        _data.SetAccountType(accountType);
                        _logger?.LogInformation("Account type: {AccountType}", accountType);

                        // Warn if not premium (librespot only supports premium)
                        if (accountType != AccountType.Premium)
                        {
                            _logger?.LogWarning("Account type is {AccountType}. Some features may not be available.", accountType);
                        }
                    }
                    else
                    {
                        _logger?.LogWarning("ProductInfo XML missing 'type' field, assuming Premium");
                        _data.SetAccountType(AccountType.Premium);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to parse ProductInfo XML");
                    // Default to Premium on parse error
                    _data.SetAccountType(AccountType.Premium);
                }
                return;
        }

        // Dispatch to event handlers
        PacketReceived?.Invoke(this, new PacketReceivedEventArgs(packetType, payload));
    }

    /// <summary>
    /// Parses ProductInfo XML format (simple element name -> text content mapping).
    /// </summary>
    /// <param name="xml">XML string from ProductInfo packet</param>
    /// <returns>Dictionary of attribute names to values</returns>
    private static Dictionary<string, string> ParseProductInfoXml(string xml)
    {
        var attributes = new Dictionary<string, string>();

        using var reader = XmlReader.Create(new System.IO.StringReader(xml), new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true
        });

        string? currentElement = null;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    currentElement = reader.Name;
                    break;

                case XmlNodeType.Text:
                    if (currentElement != null && !string.IsNullOrWhiteSpace(reader.Value))
                    {
                        attributes[currentElement] = reader.Value;
                    }
                    break;

                case XmlNodeType.EndElement:
                    currentElement = null;
                    break;
            }
        }

        return attributes;
    }

    private async Task DisconnectInternalAsync()
    {
        // Stop dispatcher
        _dispatchCts?.Cancel();
        if (_dispatchTask is not null)
        {
            try
            {
                await _dispatchTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // Cleanup transport
        var transport = _data.GetTransport();
        if (transport is not null)
        {
            await transport.DisposeAsync();
        }

        _data.SetTransport(null, null);
    }

    private void OnDisconnected()
    {
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Disposes the session and releases all resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await DisconnectInternalAsync();

        _data.Dispose();
        _httpClient.Dispose();
        _connectLock.Dispose();
        _dispatchCts?.Dispose();
    }
}

/// <summary>
/// Event args for packet received event.
/// </summary>
public sealed class PacketReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The received packet type.
    /// </summary>
    public PacketType PacketType { get; }

    /// <summary>
    /// The packet payload.
    /// </summary>
    public byte[] Payload { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketReceivedEventArgs"/> class.
    /// </summary>
    /// <param name="packetType">The packet type.</param>
    /// <param name="payload">The payload.</param>
    public PacketReceivedEventArgs(PacketType packetType, byte[] payload)
    {
        PacketType = packetType;
        Payload = payload;
    }
}
