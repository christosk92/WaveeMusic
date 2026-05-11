using System.Net.Sockets;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Wavee.Connect;
using Wavee.Connect.Commands;
using Wavee.Connect.Diagnostics;
using Wavee.Connect.Events;
using Wavee.Connect.Protocol;
using Wavee.Core.Audio;
using Wavee.Core.Authentication;
using Wavee.Core.Connection;
using Wavee.Core.Http;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Wavee.Core.Mercury;
using Wavee.Core.Time;

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
    private static readonly byte[] PongPayload = [0x00, 0x00, 0x00, 0x00];

    private readonly SessionConfig _config;
    private readonly SessionData _data;
    private readonly ILogger? _logger;
    private readonly IRemoteStateRecorder? _remoteStateRecorder;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);

    // Packet dispatcher
    private readonly Channel<(byte command, byte[] payload)> _sendQueue;
    private Task? _dispatchTask;
    private CancellationTokenSource? _dispatchCts;

    // Cached SpClient endpoint (resolved during ConnectAsync)
    private string? _spClientEndpoint;

    // Client token manager (lazy-initialized after connect)
    private ClientTokenManager? _clientTokenManager;

    // Mercury protocol manager
    private MercuryManager? _mercuryManager;

    // Keymaster token provider (for Web API scoped tokens)
    private KeymasterTokenProvider? _keymasterTokenProvider;

    // Connection state observable
    private readonly BehaviorSubject<SessionConnectionState> _connectionState = new(SessionConnectionState.Connected);
    public IObservable<SessionConnectionState> ConnectionState => _connectionState;

    // Connect subsystem
    private DealerClient? _dealerClient;
    private DeviceStateManager? _deviceStateManager;
    private ConnectCommandHandler? _commandHandler;
    private PlaybackStateManager? _playbackStateManager;

    // Audio subsystem
    private AudioKeyManager? _audioKeyManager;
    // Optional disk-backed cache injected by the app layer (see SetCacheService).
    // Flows into AudioKeyManager so AudioKeys can survive app restarts.
    private Wavee.Core.Storage.ICacheService? _cacheService;
    // Optional PlayPlay-based fallback key deriver injected by the app layer
    // (see SetPlayPlayKeyDeriver). Used by AudioKeyManager when the AP audio-key
    // channel returns a permanent error or repeated timeouts. Null = feature disabled.
    private IPlayPlayKeyDeriver? _playPlayKeyDeriver;

    // Keep-alive state machine (set by DispatchLoop, accessed by HandlePacket)
    private KeepAlive? _keepAlive;

    // Proactive AP health check: timestamp of last packet received from AP
    private DateTime _lastApPacketUtc = DateTime.UtcNow;
    private IDisposable? _dealerReconnectSubscription;
    private IDisposable? _recorderDealerStateSubscription;
    private IDisposable? _recorderConnectionIdSubscription;

    // Clock synchronization
    private SpotifyClockService? _clockService;

    // Pathfinder client (cached — one instance per session)
    private PathfinderClient? _pathfinderClient;

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

    private Session(
        SessionConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger? logger,
        IRemoteStateRecorder? remoteStateRecorder)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        _config = config;
        _logger = logger;
        _remoteStateRecorder = remoteStateRecorder;
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
    public static Session Create(
        SessionConfig config,
        IHttpClientFactory httpClientFactory,
        ILogger? logger = null,
        IRemoteStateRecorder? remoteStateRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        return new Session(config, httpClientFactory, logger, remoteStateRecorder);
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

            // 7. Start clock sync now that auth is ready
            Clock.Start();

            // 8. Initialize client token manager (needed by SpClient for spclient requests)
            _clientTokenManager = new ClientTokenManager(_httpClient, _config, _logger);

            // 8. Initialize Mercury protocol (for keymaster tokens, subscriptions, etc.)
            _mercuryManager = new MercuryManager(this, _logger);
            _keymasterTokenProvider = new KeymasterTokenProvider(
                _mercuryManager, _config, _config.DeviceId, _logger);

            // 8. Initialize Spotify Connect subsystem
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
            if (!Config.LocalSpotifyPlaybackEnabled)
            {
                _logger?.LogInformation("Transfer ignored: local Spotify playback is disabled");
                return;
            }

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
            _dealerClient = new DealerClient(
                config: new DealerClientConfig { Logger = _logger },
                remoteStateRecorder: _remoteStateRecorder);
            SubscribeRemoteStateRecorder(_dealerClient);
            await _dealerClient.ConnectAsync(this, _httpClient, cancellationToken);
            _logger?.LogDebug("DealerClient connected");

            // Proactive AP health check: when dealer reconnects, check if AP is stale
            _dealerReconnectSubscription = _dealerClient.ConnectionState
                .DistinctUntilChanged()
                .Where(s => s == Connect.Connection.ConnectionState.Connected)
                .Skip(1) // skip initial connection, only react to RE-connections
                .Subscribe(_ => OnDealerReconnected());

            // Create DeviceStateManager with configured initial volume
            _deviceStateManager = new DeviceStateManager(
                this,
                (SpClient)SpClient,
                _dealerClient,
                initialVolume: _config.InitialVolume,
                logger: _logger,
                remoteStateRecorder: _remoteStateRecorder);

            // Create command handler for processing incoming REQUESTs
            _commandHandler = new ConnectCommandHandler(_dealerClient, _logger, _remoteStateRecorder);
            _logger?.LogDebug("ConnectCommandHandler created");

            // Create playback state manager for processing cluster MESSAGEs
            _playbackStateManager = new PlaybackStateManager(_dealerClient, _logger, _remoteStateRecorder);
            _logger?.LogDebug("PlaybackStateManager created");

            // Create event service for reporting playback events. Routes through
            // gabo-receiver-service/v3/events/ — the legacy event-service paths
            // (HTTPS + Mercury) are both 404. Installation id is derived from
            // the device id for now (16-byte stable per-install). Could be
            // promoted to its own persisted random value later if Spotify
            // starts rate-limiting based on it.
            //
            // Wavee.Core can't see SessionConfig.ClientId directly (yet), so
            // fall back to the keymaster id when the config doesn't override it.
            const string KeymasterClientId = "65b708073fc0480ea92a077233ca87bd";
            var clientIdHex = string.IsNullOrEmpty(_config.ClientId) ? KeymasterClientId : _config.ClientId;
            var installationId = DeriveInstallationId(_config.DeviceId);
            // Match what desktop Spotify ships in context_device_desktop —
            // BIOS-reported manufacturer / product, Windows machine SID, and
            // 3-part OS version (no trailing .0 revision). Empty values on
            // non-Windows / locked-down hosts are still better than the
            // obviously-fake "Wavee" / "Wavee Desktop" we used to send.
            var manufacturer = WindowsHardwareInfo.GetManufacturer();
            var model = WindowsHardwareInfo.GetModel();
            var machineSid = WindowsHardwareInfo.GetMachineSid();
            var osVersion = WindowsHardwareInfo.GetOsVersionThreePart();
            // Stable per-app-process session id Spotify expects in
            // context_application_desktop.session_id (16 random bytes,
            // generated once per Session instance — same lifetime as the
            // desktop client's "session id" semantics).
            var appSessionId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            _eventService = new EventService(
                (SpClient)SpClient,
                deviceIdHex: _config.DeviceId,
                clientIdHex: clientIdHex,
                installationId: installationId,
                osVersion: osVersion,
                deviceManufacturer: !string.IsNullOrEmpty(manufacturer) ? manufacturer : "Microsoft Corporation",
                deviceModel: !string.IsNullOrEmpty(model) ? model : "PC",
                osLevelDeviceId: !string.IsNullOrEmpty(machineSid) ? machineSid : _config.DeviceId,
                appSessionId: appSessionId,
                logger: _logger);
            _logger?.LogDebug("EventService created (gabo-receiver transport)");

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
            DisposeRemoteStateRecorderSubscriptions();
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
    public ISpClient SpClient => new SpClient(
        this,
        _httpClient,
        _spClientEndpoint ?? "spclient.wg.spotify.com:443",
        _clientTokenManager,
        _logger,
        _remoteStateRecorder);

    /// <summary>
    /// Gets the resolved SpClient endpoint URL.
    /// </summary>
    public string SpClientUrl => _spClientEndpoint ?? "spclient.wg.spotify.com:443";

    /// <summary>
    /// Gets the Mercury protocol manager for internal Spotify requests.
    /// </summary>
    public MercuryManager? Mercury => _mercuryManager;

    /// <summary>
    /// Gets the Keymaster token provider for scoped Web API access tokens.
    /// </summary>
    public KeymasterTokenProvider? Keymaster => _keymasterTokenProvider;

    /// <summary>
    /// Gets the clock synchronization service for correcting local-vs-server time skew.
    /// Lazily initialized on first access after SpClient is available.
    /// </summary>
    public SpotifyClockService Clock => _clockService ??= new SpotifyClockService(SpClient, _logger);

    /// <summary>
    /// Gets the Pathfinder client for GraphQL API requests (search, browse, etc).
    /// </summary>
    /// <remarks>
    /// Pathfinder uses the api-partner.spotify.com endpoint for GraphQL queries.
    /// Access tokens are obtained automatically via login5.
    /// </remarks>
    /// <returns>PathfinderClient instance.</returns>
    public IPathfinderClient Pathfinder => _pathfinderClient ??= new PathfinderClient(
        this,
        _httpClient,
        clientTokenManager: _clientTokenManager,
        logger: _logger);

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
            _audioKeyManager ??= new AudioKeyManager(this, _logger, _cacheService, _playPlayKeyDeriver);
            return _audioKeyManager;
        }
    }

    /// <summary>
    /// Injects a disk-backed cache for AudioKey persistence. Call this early —
    /// before the first track plays — so <see cref="AudioKeys"/> is constructed
    /// with the cache reference. Calling it after AudioKeys has already been
    /// accessed is a no-op (the in-process cache stays memory-only).
    /// </summary>
    public void SetCacheService(Wavee.Core.Storage.ICacheService cacheService)
    {
        _cacheService = cacheService;
    }

    /// <summary>
    /// Injects a PlayPlay key-derivation fallback. Same lifecycle rule as
    /// <see cref="SetCacheService"/>: register before the first track plays,
    /// otherwise <see cref="AudioKeys"/> will already have been constructed
    /// without the reference and the fallback will not fire.
    /// </summary>
    /// <remarks>
    /// The implementation is expected to live in <c>Wavee.PlayPlay</c> and to
    /// run a CPU emulator over a copy of <c>Spotify.dll</c>. If no deriver is
    /// registered, <see cref="AudioKeyManager"/> behaves exactly as before:
    /// permanent AP errors (<c>0x0001</c>) and timeout exhaustion remain hard
    /// failures.
    /// </remarks>
    public void SetPlayPlayKeyDeriver(IPlayPlayKeyDeriver playPlayKeyDeriver)
    {
        ArgumentNullException.ThrowIfNull(playPlayKeyDeriver);
        _playPlayKeyDeriver = playPlayKeyDeriver;
    }

    public UserData UserData => _data.UserData;

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
    /// Activates this device with the last known player state so Spotify preserves the current track.
    /// Used when taking over playback from a ghost/disconnected device.
    /// </summary>
    public async Task<bool> ActivateWithCurrentStateAsync(CancellationToken cancellationToken = default)
    {
        if (_deviceStateManager == null || _playbackStateManager == null)
            return false;
        if (!Config.LocalSpotifyPlaybackEnabled)
            return false;

        // Override status to Playing — user wants to resume, not stay paused
        var currentState = _playbackStateManager.CurrentState with { Status = PlaybackStatus.Playing };
        var playerState = PlaybackStateHelpers.ToPlayerState(
            currentState,
            Config.DeviceId,
            Config.LocalSpotifyPlaybackEnabled);
        await _deviceStateManager.SetActiveWithStateAsync(playerState, cancellationToken);
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
        // Fast path: valid token already cached — no lock needed.
        var token = _data.GetAccessToken();
        if (token != null && !token.ShouldRefresh())
            return token;

        // Serialize concurrent refresh attempts so only one login5 call runs at a time.
        // The double-check inside the lock handles the "thundering herd" at startup where
        // SpClient, DealerClient, ClockService, etc. all call GetAccessTokenAsync
        // simultaneously and would otherwise each trigger a separate login5 flow.
        await _tokenRefreshLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check: another caller may have refreshed while we waited.
            token = _data.GetAccessToken();
            if (token != null && !token.ShouldRefresh())
                return token;

            _logger?.LogDebug("Access token expired or missing, refreshing via login5");

            // If the session hasn't finished authenticating yet (startup race), wait
            // rather than throwing immediately — the caller already has a valid
            // CancellationToken so this is bounded by the caller's timeout.
            await _data.WaitForAuthAsync(cancellationToken);

            var storedCredentials = _data.GetStoredCredentials()!;

            // Exchange for access token via login5
            var login5 = _data.GetLogin5Client();
            token = await login5.GetAccessTokenAsync(
                storedCredentials.Username!,
                storedCredentials.AuthData,
                clientToken: null,
                cancellationToken);

            _data.SetAccessToken(token);
            _logger?.LogInformation("Access token refreshed (expires {ExpiresAt})", token.ExpiresAt);
        }
        finally
        {
            _tokenRefreshLock.Release();
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
        _keepAlive = new KeepAlive(_logger);

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

                // Evaluate keep-alive state machine
                var action = _keepAlive.Evaluate();

                switch (action)
                {
                    case KeepAliveAction.SendPong:
                        _sendQueue.Writer.TryWrite(((byte)PacketType.Pong, PongPayload));
                        _logger?.LogDebug("Keep-alive: sending Pong (delayed response)");
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
                    _lastApPacketUtc = DateTime.UtcNow;
                    HandlePacket((PacketType)cmd, payload);
                }
                // else: timeout, receiveTask stays assigned and will be checked on next iteration
            }
        }
        catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException se && se.ErrorCode == 10054)
        {
            // Expected: Spotify forcibly closed connection (normal during session timeout or server maintenance)
            _logger?.LogInformation("Connection closed by server (reset)");
            await DisconnectInternalAsync();
            OnDisconnected();
        }
        catch (IOException ex)
        {
            _logger?.LogWarning("AP connection lost (IOException): {Message}", ex.Message);
            await DisconnectInternalAsync();
            OnDisconnected();
        }
        catch (ApCodecException ex)
        {
            // MAC verification failure or malformed frame — the shannon stream is no
            // longer trustworthy, but the AP itself may be fine. Tear down, then fire
            // a background reconnect so audio-key requests can resume instead of
            // leaving the session in a permanently disconnected state.
            _logger?.LogCritical(ex, "AP codec fault; triggering automatic reconnect");
            await DisconnectInternalAsync();
            OnDisconnected();
            TriggerBackgroundReconnect("codec fault");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Packet dispatcher failed");
            await DisconnectInternalAsync();
            OnDisconnected();
        }
        finally
        {
            _keepAlive = null;
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
                // Server Ping received — transition keep-alive to PendingPong.
                // Pong will be sent after 60s delay (matching librespot protocol).
                _keepAlive?.OnPingReceived();

                // Extract server timestamp for time_delta calculation (like librespot)
                if (payload.Length >= 4)
                {
                    var serverTimestamp = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload);
                    var localTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var timeDelta = serverTimestamp - localTimestamp;
                    _logger?.LogTrace("Received Ping from server (timestamp={ServerTs}, delta={Delta}s)",
                        serverTimestamp, timeDelta);
                }
                else
                {
                    _logger?.LogTrace("Received Ping from server (no timestamp)");
                }
                return;

            case PacketType.PongAck:
                // Server acknowledges our Pong — keep-alive cycle complete
                _keepAlive?.OnPongAckReceived();
                _logger?.LogTrace("Received PongAck from server (keep-alive confirmed)");
                return;

            case PacketType.Pong:
                // Direct Pong from server (rare, treat as PongAck)
                _keepAlive?.OnPongAckReceived();
                _logger?.LogTrace("Received Pong from server");
                return;

            case PacketType.MercuryReq:
            case PacketType.MercurySub:
            case PacketType.MercuryUnsub:
            case PacketType.MercuryEvent:
            case PacketType.Unknown0xb6:
                _mercuryManager?.DispatchPacket((byte)packetType, payload);
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

                    // client-deprecated = server telling us our BuildInfo.Version is
                    // older than current desktop. Still serves traffic, but worth
                    // surfacing — if it starts correlating with breakage we know where
                    // to look. See SpotifyClientIdentity.HandshakeBuildVersion.
                    var isClientDeprecated = attributes.TryGetValue("client-deprecated", out var deprecatedStr)
                        && deprecatedStr == "1";
                    if (isClientDeprecated)
                    {
                        _logger?.LogWarning(
                            "Spotify flagged this client as deprecated (ProductInfo.client-deprecated=1). " +
                            "Bump SpotifyClientIdentity.HandshakeBuildVersion when refreshing desktop parity.");
                    }

                    // loudness-levels = ReplayGain targets for quiet/normal/loud modes.
                    // Parse here so the audio pipeline can combine with per-track gain.
                    attributes.TryGetValue("loudness-levels", out var loudnessLevelsRaw);
                    var loudnessLevels = LoudnessLevels.TryParse(loudnessLevelsRaw);
                    if (loudnessLevels == null && !string.IsNullOrEmpty(loudnessLevelsRaw))
                    {
                        _logger?.LogDebug("Unparsable loudness-levels='{Raw}' — falling back to per-track gain only",
                            loudnessLevelsRaw);
                    }

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
                            VideoKeyframeUrl = videoKeyframeUrl,
                            IsClientDeprecated = isClientDeprecated,
                            LoudnessLevels = loudnessLevels,
                        };
                        _data.SetUserData(updatedUserData);
                        _logger?.LogDebug("Updated UserData with ProductInfo fields (deprecated={Deprecated}, loudness={Loudness})",
                            isClientDeprecated, loudnessLevels != null ? "parsed" : "null");
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

    /// <summary>
    /// Explicitly disconnects the session (closes AP transport, stops the dispatcher, fires
    /// <see cref="Disconnected"/>). Intended for sign-out — callers can re-authenticate
    /// by calling <see cref="ConnectAsync"/> again with new credentials.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsConnected()) return;

            await DisconnectInternalAsync();
            _connectionState.OnNext(SessionConnectionState.Disconnected);
            OnDisconnected();
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Reconnects to the Access Point and resets audio key sequence numbers.
    /// Used when AudioKey requests time out repeatedly (stale connection).
    /// </summary>
    /// <remarks>
    /// This method:
    /// 1. Closes current TCP connection
    /// 2. Attempts to reconnect to a new AP
    /// 3. Re-authenticates using stored credentials
    /// 4. Resets AudioKeyManager sequence to 0
    /// 5. Restarts packet dispatcher
    /// </remarks>
    public async Task ReconnectApAsync(CancellationToken cancellationToken = default)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            _logger?.LogWarning("Reconnecting to Access Point due to stale connection");
            _connectionState.OnNext(SessionConnectionState.Reconnecting);

            // 1. Close old transport FIRST (unblocks socket reads in dispatcher)
            var oldTransport = _data.GetTransport();
            if (oldTransport is not null)
            {
                await oldTransport.DisposeAsync();
                _logger?.LogDebug("Closed old AP transport");
            }
            _data.SetTransport(null, null);

            // 2. Now stop dispatcher (socket is unblocked, will exit cleanly via ObjectDisposedException handler)
            _dispatchCts?.Cancel();
            if (_dispatchTask is not null)
            {
                try
                {
                    await _dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (TimeoutException)
                {
                    _logger?.LogWarning("Dispatcher did not stop within timeout");
                }
            }
            _logger?.LogDebug("Stopped packet dispatcher");

            // 3. Get stored credentials for re-authentication
            var storedCredentials = _data.GetStoredCredentials();
            if (storedCredentials == null)
            {
                throw new SessionException(
                    SessionFailureReason.AuthenticationFailed,
                    "Cannot reconnect: no stored credentials available");
            }

            // 4. Resolve APs
            var aps = await ApResolver.ResolveAsync(_httpClient, _logger, cancellationToken);

            // 5. Try each AP until one succeeds
            ApTransport? newTransport = null;
            string? connectedAp = null;
            Exception? lastException = null;

            foreach (var ap in aps)
            {
                try
                {
                    _logger?.LogDebug("Attempting reconnect to AP: {Ap}", ap);
                    newTransport = await ConnectToApAsync(ap, cancellationToken);
                    connectedAp = ap;
                    _logger?.LogInformation("Reconnected to Access Point: {Ap}", ap);
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to reconnect to {Ap}", ap);
                    lastException = ex;
                }
            }

            if (newTransport is null || connectedAp is null)
            {
                throw new SessionException(
                    SessionFailureReason.ConnectionFailed,
                    "Failed to reconnect to any Access Point",
                    lastException);
            }

            // 6. Re-authenticate
            _logger?.LogDebug("Re-authenticating with stored credentials");
            var reusableCredentials = await Authenticator.AuthenticateAsync(
                newTransport,
                storedCredentials,
                _config.DeviceId,
                _logger,
                cancellationToken);

            // 7. Update transport
            _data.SetTransport(newTransport, connectedAp);
            _data.SetStoredCredentials(reusableCredentials);

            // 8. Reset AudioKeyManager sequence
            _audioKeyManager?.ResetSequence();

            // 9. Restart dispatcher (dispose old CTS to prevent leak)
            _dispatchCts?.Dispose();
            _dispatchCts = new CancellationTokenSource();
            _dispatchTask = Task.Run(() => DispatchLoop(_dispatchCts.Token), _dispatchCts.Token);
            _logger?.LogDebug("Restarted packet dispatcher");

            _logger?.LogInformation("AP reconnection successful");

            // Restart clock sync after reconnection
            Clock.Start();

            _connectionState.OnNext(SessionConnectionState.Connected);
        }
        catch
        {
            _connectionState.OnNext(SessionConnectionState.Disconnected);
            throw;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Called when the dealer WebSocket reconnects. Checks if the AP TCP connection
    /// is likely stale and proactively reconnects to avoid AudioKey timeout delays.
    /// </summary>
    private async void OnDealerReconnected()
    {
        const int stalenessThresholdSeconds = 10;
        var elapsed = DateTime.UtcNow - _lastApPacketUtc;

        if (elapsed.TotalSeconds < stalenessThresholdSeconds)
        {
            _logger?.LogDebug(
                "Dealer reconnected; AP last packet {Elapsed:F1}s ago — AP appears healthy",
                elapsed.TotalSeconds);
            return;
        }

        _logger?.LogWarning(
            "Dealer reconnected; AP last packet {Elapsed:F1}s ago — proactively reconnecting AP",
            elapsed.TotalSeconds);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await ReconnectApAsync(cts.Token);
            _logger?.LogInformation("Proactive AP reconnection succeeded after dealer reconnect");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Proactive AP reconnection failed after dealer reconnect");
        }
    }

    private void SubscribeRemoteStateRecorder(DealerClient dealerClient)
    {
        if (_remoteStateRecorder == null) return;

        DisposeRemoteStateRecorderSubscriptions();

        _recorderDealerStateSubscription = dealerClient.ConnectionState
            .DistinctUntilChanged()
            .Skip(1)
            .Subscribe(state =>
            {
                _remoteStateRecorder.Record(
                    kind: RemoteStateEventKind.DealerLifecycle,
                    direction: RemoteStateDirection.Internal,
                    summary: $"Dealer {state}");
            });

        string? lastConnectionId = null;
        _recorderConnectionIdSubscription = dealerClient.ConnectionId
            .Where(id => !string.IsNullOrEmpty(id))
            .DistinctUntilChanged()
            .Subscribe(id =>
            {
                if (string.IsNullOrEmpty(id)) return;

                var kind = lastConnectionId == null
                    ? RemoteStateEventKind.ConnectionIdAcquired
                    : RemoteStateEventKind.ConnectionIdChanged;
                var summary = lastConnectionId == null
                    ? $"acquired connectionId={id}"
                    : $"connectionId changed: {lastConnectionId} -> {id}";

                _remoteStateRecorder.Record(
                    kind: kind,
                    direction: RemoteStateDirection.Internal,
                    summary: summary,
                    correlationId: id);

                lastConnectionId = id;
            });
    }

    private void DisposeRemoteStateRecorderSubscriptions()
    {
        _recorderDealerStateSubscription?.Dispose();
        _recorderDealerStateSubscription = null;
        _recorderConnectionIdSubscription?.Dispose();
        _recorderConnectionIdSubscription = null;
    }

    private void OnDisconnected()
    {
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fires a fire-and-forget AP reconnect on a pool thread. Used by dispatcher
    /// fault paths that cannot call <see cref="ReconnectApAsync"/> inline because
    /// <c>ReconnectApAsync</c> cancels and awaits the dispatch task itself.
    /// </summary>
    private void TriggerBackgroundReconnect(string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await ReconnectApAsync(cts.Token);
                _logger?.LogInformation("Background AP reconnect succeeded after {Reason}", reason);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Background AP reconnect failed after {Reason}", reason);
            }
        });
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

        _dealerReconnectSubscription?.Dispose();
        _dealerReconnectSubscription = null;
        DisposeRemoteStateRecorderSubscriptions();

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

        _clockService?.Dispose();
        _clockService = null;

        await DisconnectInternalAsync();

        _data.Dispose();
        _httpClient.Dispose();
        _connectLock.Dispose();
        _dispatchCts?.Dispose();
    }

    /// <summary>
    /// Derives a stable 16-byte installation id from the device id. Spotify
    /// expects a per-install random value here; deriving from the device id
    /// gives the same stability without needing an extra persisted setting.
    /// SHA-256 of the device id, truncated to 16 bytes.
    /// </summary>
    private static byte[] DeriveInstallationId(string deviceIdHex)
    {
        var input = System.Text.Encoding.UTF8.GetBytes(deviceIdHex ?? string.Empty);
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        return hash.AsSpan(0, 16).ToArray();
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
