using System.Net.Sockets;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Events;
using Wavee.Connect.Protocol;
using Wavee.Core.Audio;
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
public sealed class Session : ISession, IAsyncDisposable
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

    // Cached SpClient endpoint (resolved during ConnectAsync)
    private string? _spClientEndpoint;

    // Connect subsystem
    private DealerClient? _dealerClient;
    private DeviceStateManager? _deviceStateManager;
    private ConnectCommandHandler? _commandHandler;
    private PlaybackStateManager? _playbackStateManager;

    // Audio subsystem
    private AudioKeyManager? _audioKeyManager;

    // Event subsystem
    private EventService? _eventService;

    /// <summary>
    /// Raised when a packet is received from the server.
    /// </summary>
    public event EventHandler<PacketReceivedEventArgs>? PacketReceived;

    /// <summary>
    /// Raised when the session is disconnected.
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// Gets the session configuration.
    /// </summary>
    public SessionConfig Config => _config;

    private Session(SessionConfig config, IHttpClientFactory httpClientFactory, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _config = config;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("Wavee");
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
    /// <param name="httpClientFactory">HTTP client factory for creating HttpClient instances.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <returns>A new session instance.</returns>
    public static Session Create(SessionConfig config, IHttpClientFactory httpClientFactory, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        return new Session(config, httpClientFactory, logger);
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

            // 1. Resolve Access Points and SpClient endpoints
            var aps = await ApResolver.ResolveAsync(_httpClient, _logger, cancellationToken);
            var spClientEndpoints = await ApResolver.ResolveSpclientAsync(_httpClient, _logger, cancellationToken);
            _spClientEndpoint = spClientEndpoints.FirstOrDefault() ?? "spclient.wg.spotify.com:443";
            _logger?.LogDebug("Using SpClient endpoint: {Endpoint}", _spClientEndpoint);

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

            // 7. Initialize Spotify Connect subsystem
            await InitializeConnectSubsystemAsync(cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Wires up Connect command handlers and state update subscriptions using Rx.NET.
    /// </summary>
    private void WireUpConnectHandlers()
    {
        if (_commandHandler == null || _playbackStateManager == null)
            return;

        _logger?.LogDebug("Wiring up Connect state subscriptions");

        // NOTE: Command handlers (Play, Pause, Resume, etc.) are now handled by AudioPipeline
        // via its SubscribeToCommands() method. Session only handles state update subscriptions.

        // ================================================================
        // STATE UPDATE SUBSCRIPTIONS (MESSAGEs) - Reactive state tracking
        // ================================================================

        _playbackStateManager.TrackChanged.Subscribe(state =>
        {
            if (state.Track != null)
            {
                _logger?.LogInformation("Track changed: {Title} - {Artist}",
                    state.Track.Title, state.Track.Artist);
            }
        });

        _playbackStateManager.PlaybackStatusChanged.Subscribe(state =>
        {
            _logger?.LogInformation("Playback status changed: {Status}", state.Status);
        });

        _playbackStateManager.PositionChanged.Subscribe(state =>
        {
            _logger?.LogDebug("Position changed: {Position}ms / {Duration}ms",
                state.PositionMs, state.DurationMs);
        });

        _playbackStateManager.ActiveDeviceChanged.Subscribe(state =>
        {
            _logger?.LogInformation("Active device changed: {DeviceId}", state.ActiveDeviceId);
        });

        _playbackStateManager.OptionsChanged.Subscribe(state =>
        {
            _logger?.LogInformation("Playback options changed: shuffle={Shuffle}, repeat={Repeat}",
                state.Options.Shuffling, state.Options.RepeatingContext);
        });

        // ================================================================
        // DEVICE ACTIVATION - Activate when transfer command received
        // ================================================================

        // Activate device when user selects it in Spotify (transfer command)
        _commandHandler.TransferCommands.Subscribe(async cmd =>
        {
            _logger?.LogInformation("Transfer command received from {Device} - activating device", cmd.SenderDeviceId);
            if (_deviceStateManager != null)
            {
                await _deviceStateManager.SetActiveAsync(true);
            }
        });

        _logger?.LogDebug("Connect handlers wired up successfully");
    }

    /// <summary>
    /// Initializes the Spotify Connect subsystem (DealerClient and DeviceStateManager).
    /// </summary>
    private async Task InitializeConnectSubsystemAsync(CancellationToken cancellationToken)
    {
        // Skip if Connect is disabled in config
        if (!_config.EnableConnect)
        {
            _logger?.LogInformation("Spotify Connect subsystem disabled by configuration");
            return;
        }

        try
        {
            _logger?.LogDebug("Initializing Spotify Connect subsystem");

            // Create and connect DealerClient with config containing logger
            _dealerClient = new DealerClient(config: new DealerClientConfig { Logger = _logger });
            await _dealerClient.ConnectAsync(this, _httpClient, cancellationToken);
            _logger?.LogDebug("DealerClient connected");

            // Create DeviceStateManager with configured initial volume
            _deviceStateManager = new DeviceStateManager(
                this,
                SpClient,
                _dealerClient,
                initialVolume: _config.InitialVolume,
                logger: _logger);

            // Create command handler for processing incoming REQUESTs
            _commandHandler = new ConnectCommandHandler(_dealerClient, _logger);
            _logger?.LogDebug("ConnectCommandHandler created");

            // Create playback state manager for processing cluster MESSAGEs
            _playbackStateManager = new PlaybackStateManager(_dealerClient, _logger);
            _logger?.LogDebug("PlaybackStateManager created");

            // Create event service for reporting playback events (artist payouts)
            _eventService = new EventService(SpClient, _logger);
            _logger?.LogDebug("EventService created");

            // Signal that subscribers are ready - flush any queued PUT state responses
            _dealerClient.StartProcessingMessages();
            _logger?.LogDebug("DealerClient message processing started");

            // Wire up command handlers and state subscriptions
            WireUpConnectHandlers();

            _logger?.LogInformation("Spotify Connect subsystem initialized - device is now visible");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize Spotify Connect subsystem");
            // Non-fatal: session can still work without Connect
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
    /// Gets the current preferred locale override (or null if using Spotify's default).
    /// </summary>
    /// <returns>The 2-character locale code or null.</returns>
    public string? GetPreferredLocale() => _data.GetPreferredLocale();

    /// <summary>
    /// Gets the SpClient for making authenticated metadata requests.
    /// </summary>
    /// <remarks>
    /// SpClient automatically obtains and refreshes access tokens using login5.
    /// The endpoint is resolved during ConnectAsync(). If accessed before connection,
    /// a default fallback endpoint is used.
    /// </remarks>
    /// <returns>SpClient instance.</returns>
    public SpClient SpClient => new SpClient(
        this,
        _httpClient,
        _spClientEndpoint ?? "spclient.wg.spotify.com:443",
        _logger);

    /// <summary>
    /// Gets the resolved SpClient endpoint URL.
    /// </summary>
    public string SpClientUrl => _spClientEndpoint ?? "spclient.wg.spotify.com:443";

    /// <summary>
    /// Gets the Spotify Connect dealer client for real-time communication.
    /// </summary>
    /// <remarks>
    /// The dealer client provides WebSocket connection to Spotify's dealer service
    /// for real-time messages, requests, and connection ID management.
    /// Available only if EnableConnect is true in SessionConfig.
    /// </remarks>
    /// <returns>DealerClient instance, or null if Connect is disabled.</returns>
    public DealerClient? Dealer => _dealerClient;

    /// <summary>
    /// Gets the Spotify Connect device state manager.
    /// </summary>
    /// <remarks>
    /// DeviceStateManager coordinates device state with Spotify's cloud API,
    /// including volume control, active state, and device announcements.
    /// Available only if EnableConnect is true in SessionConfig.
    /// </remarks>
    /// <returns>DeviceStateManager instance, or null if Connect is disabled.</returns>
    public DeviceStateManager? DeviceState => _deviceStateManager;

    /// <summary>
    /// Gets the Spotify Connect command handler for processing remote control commands.
    /// </summary>
    /// <remarks>
    /// ConnectCommandHandler processes incoming REQUEST messages from Spotify apps
    /// (Play, Pause, Resume, Seek, SetVolume, etc.) and provides reactive observables
    /// for each command type. Available only if EnableConnect is true in SessionConfig.
    /// </remarks>
    /// <returns>ConnectCommandHandler instance, or null if Connect is disabled.</returns>
    public ConnectCommandHandler? CommandHandler => _commandHandler;

    /// <summary>
    /// Gets the Spotify Connect playback state manager for tracking remote state updates.
    /// </summary>
    /// <remarks>
    /// PlaybackStateManager processes cluster update MESSAGEs and provides reactive
    /// observables for state changes (track, position, status, options, etc.).
    /// Available only if EnableConnect is true in SessionConfig.
    /// </remarks>
    /// <returns>PlaybackStateManager instance, or null if Connect is disabled.</returns>
    public PlaybackStateManager? PlaybackState => _playbackStateManager;

    /// <summary>
    /// Gets the event service for reporting playback events to Spotify.
    /// </summary>
    /// <remarks>
    /// EventService sends playback events (TrackTransition, NewPlaybackId, etc.) to Spotify
    /// for analytics and artist payouts. Events are sent asynchronously in the background.
    /// Available only if EnableConnect is true in SessionConfig.
    /// </remarks>
    /// <returns>EventService instance, or null if Connect is disabled.</returns>
    public EventService? Events => _eventService;

    /// <summary>
    /// Gets the AudioKeyManager for requesting audio decryption keys.
    /// </summary>
    /// <remarks>
    /// AudioKeyManager handles the request/response protocol for obtaining
    /// AES-128 keys used to decrypt Spotify audio files. Keys are cached
    /// internally and automatically managed.
    /// </remarks>
    /// <returns>AudioKeyManager instance.</returns>
    public AudioKeyManager AudioKeys
    {
        get
        {
            _audioKeyManager ??= new AudioKeyManager(this, _logger);
            return _audioKeyManager;
        }
    }

    /// <summary>
    /// Sets the device active or inactive state for Spotify Connect.
    /// </summary>
    /// <param name="active">True to make device visible and controllable, false to hide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if state was changed, false if Connect is disabled.</returns>
    /// <exception cref="SpClientException">Thrown if the PUT state request fails.</exception>
    public async Task<bool> SetDeviceActiveAsync(bool active, CancellationToken cancellationToken = default)
    {
        if (_deviceStateManager == null)
            return false;

        await _deviceStateManager.SetActiveAsync(active, cancellationToken);
        return true;
    }

    /// <summary>
    /// Sets the device volume (Spotify's 0-65535 range).
    /// </summary>
    /// <param name="volume">Volume level (0-65535).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if volume was set, false if Connect is disabled.</returns>
    /// <exception cref="SpClientException">Thrown if the PUT state request fails.</exception>
    public async Task<bool> SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        if (_deviceStateManager == null)
            return false;

        await _deviceStateManager.SetVolumeAsync(volume, cancellationToken);
        return true;
    }

    /// <summary>
    /// Gets the current device volume as a percentage (0-100).
    /// </summary>
    /// <returns>Volume percentage, or null if Connect is disabled.</returns>
    public int? GetVolumePercentage()
    {
        if (_deviceStateManager == null)
            return null;

        return ConnectStateHelpers.VolumeToPercentage(_deviceStateManager.CurrentVolume);
    }

    /// <summary>
    /// Sets the device volume from a percentage (0-100).
    /// </summary>
    /// <param name="percentage">Volume percentage (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if volume was set, false if Connect is disabled.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if percentage is not 0-100.</exception>
    /// <exception cref="SpClientException">Thrown if the PUT state request fails.</exception>
    public async Task<bool> SetVolumePercentageAsync(int percentage, CancellationToken cancellationToken = default)
    {
        if (percentage < 0 || percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage), "Volume percentage must be 0-100");

        if (_deviceStateManager == null)
            return false;

        var volume = ConnectStateHelpers.VolumeFromPercentage(percentage);
        await _deviceStateManager.SetVolumeAsync(volume, cancellationToken);
        return true;
    }

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

    /// <summary>
    /// Updates the preferred locale and publishes it to Spotify's servers.
    /// </summary>
    /// <param name="locale">2-character ISO 639-1 language code (e.g., "en", "es", "fr").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">Thrown if locale is not exactly 2 characters.</exception>
    /// <exception cref="SessionException">Thrown if session is not connected.</exception>
    public async Task UpdateLocaleAsync(string locale, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale, nameof(locale));

        if (locale.Length != 2)
            throw new ArgumentException("Locale must be exactly 2 characters (ISO 639-1 language code)", nameof(locale));

        if (!_data.IsConnected())
            throw new SessionException(SessionFailureReason.Disposed, "Session not connected");

        _logger?.LogDebug("Updating preferred locale to: {Locale}", locale);

        // Send Unknown_0x0f packet (20 random bytes, purpose unknown but required by protocol)
        var randomBytes = new byte[20];
        Random.Shared.NextBytes(randomBytes);
        await SendAsync(PacketType.Unknown0x0f, randomBytes, cancellationToken);
        _logger?.LogTrace("Sent Unknown_0x0f packet");

        // Send PreferredLocale packet
        var localeBytes = System.Text.Encoding.UTF8.GetBytes(locale);
        var payload = new byte[5 + 16 + localeBytes.Length];

        // Header: 0x00 0x00 0x10 0x00 0x02
        payload[0] = 0x00;
        payload[1] = 0x00;
        payload[2] = 0x10;
        payload[3] = 0x00;
        payload[4] = 0x02;

        // "preferred-locale" string (16 bytes)
        var keyBytes = System.Text.Encoding.UTF8.GetBytes("preferred-locale");
        Array.Copy(keyBytes, 0, payload, 5, keyBytes.Length);

        // Locale value (2 bytes)
        Array.Copy(localeBytes, 0, payload, 5 + 16, localeBytes.Length);

        await SendAsync(PacketType.PreferredLocale, payload, cancellationToken);

        // Update session state
        _data.SetPreferredLocale(locale);

        _logger?.LogInformation("Published preferred locale to Spotify: {Locale}", locale);
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

        // Reuse the same receive task across loop iterations to avoid concurrent PipeReader access
        // This is critical: creating a new ReceiveAsync while the previous one is pending causes corruption
        Task<(byte command, byte[] payload)?>? receiveTask = null;

        try
        {
            _logger?.LogDebug("Packet dispatcher started");

            while (!cancellationToken.IsCancellationRequested)
            {
                var transport = _data.GetTransport();
                if (transport is null || !_data.IsConnected())
                {
                    _logger?.LogWarning("Transport became null or session disconnected, stopping dispatcher");
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
                    try
                    {
                        await transport.SendAsync(cmd, payload, cancellationToken);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Transport was disposed, connection is closing
                        _logger?.LogDebug("Transport disposed during send");
                        await DisconnectInternalAsync();
                        OnDisconnected();
                        return;
                    }
                }

                // Receive packets (with timeout, non-exception based)
                // Only start a new receive if we don't have one pending
                if (receiveTask is null)
                {
                    try
                    {
                        receiveTask = transport.ReceiveAsync(cancellationToken).AsTask();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Transport was disposed, connection is closing
                        _logger?.LogDebug("Transport disposed during receive setup");
                        await DisconnectInternalAsync();
                        OnDisconnected();
                        return;
                    }
                }

                var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);

                var completedTask = await Task.WhenAny(receiveTask, timeoutTask);

                if (completedTask == receiveTask)
                {
                    // Packet received
                    (byte command, byte[] payload)? packet;
                    try
                    {
                        packet = await receiveTask;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Transport was disposed, connection is closing
                        _logger?.LogDebug("Transport disposed during receive");
                        await DisconnectInternalAsync();
                        OnDisconnected();
                        return;
                    }
                    finally
                    {
                        // Clear the task so a new one is started on the next iteration
                        receiveTask = null;
                    }

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
                // else: timeout, receiveTask stays assigned and will be checked on next iteration
            }
        }
        catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException se && se.ErrorCode == 10054)
        {
            // Expected: Spotify forcibly closed connection (normal during session timeout or server maintenance)
            _logger?.LogInformation("Connection closed by server");
            await DisconnectInternalAsync();
            OnDisconnected();
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

            case PacketType.AesKey:
            case PacketType.AesKeyError:
                // Log at DEBUG level for visibility (AesKey responses are important for debugging)
                _logger?.LogDebug("Received {PacketType} packet ({Size} bytes)", packetType, payload.Length);
                if (_audioKeyManager is null)
                {
                    _logger?.LogWarning("AudioKeyManager is null, cannot dispatch {PacketType} packet", packetType);
                    return;
                }
                _audioKeyManager.DispatchPacket(packetType, payload);
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

                    // Extract account type
                    var accountType = AccountType.Premium;
                    if (attributes.TryGetValue("type", out var accountTypeStr))
                    {
                        accountType = accountTypeStr.ToLowerInvariant() switch
                        {
                            "premium" => AccountType.Premium,
                            "free" => AccountType.Free,
                            "family" => AccountType.Family,
                            "artist" => AccountType.Artist,
                            _ => AccountType.Unknown
                        };
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
                    }

                    // Extract additional fields
                    attributes.TryGetValue("head-files-url", out var headFilesUrl);
                    attributes.TryGetValue("image-url", out var imageUrl);
                    attributes.TryGetValue("preferred-locale", out var preferredLocale);
                    attributes.TryGetValue("video-keyframe-url", out var videoKeyframeUrl);

                    var filterExplicitContent = attributes.TryGetValue("filter-explicit-content", out var filterStr)
                        && filterStr == "1";

                    // Update UserData with ProductInfo fields
                    var currentUserData = _data.GetUserData();
                    if (currentUserData != null)
                    {
                        var updatedUserData = currentUserData with
                        {
                            AccountType = accountType,
                            HeadFilesUrl = headFilesUrl,
                            ImageUrl = imageUrl,
                            FilterExplicitContent = filterExplicitContent,
                            PreferredLocale = preferredLocale,
                            VideoKeyframeUrl = videoKeyframeUrl
                        };
                        _data.SetUserData(updatedUserData);
                        _logger?.LogDebug("Updated UserData with ProductInfo fields");
                    }

                    // Set account type TCS for backward compatibility
                    _data.SetAccountType(accountType);
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
        // Dispose Connect subsystem first (reverse order of creation)
        if (_playbackStateManager is not null)
        {
            await _playbackStateManager.DisposeAsync();
            _playbackStateManager = null;
        }

        if (_commandHandler is not null)
        {
            await _commandHandler.DisposeAsync();
            _commandHandler = null;
        }

        if (_deviceStateManager is not null)
        {
            await _deviceStateManager.DisposeAsync();
            _deviceStateManager = null;
        }

        if (_dealerClient is not null)
        {
            await _dealerClient.DisposeAsync();
            _dealerClient = null;
        }

        if (_audioKeyManager is not null)
        {
            await _audioKeyManager.DisposeAsync();
            _audioKeyManager = null;
        }

        if (_eventService is not null)
        {
            await _eventService.DisposeAsync();
            _eventService = null;
        }

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
