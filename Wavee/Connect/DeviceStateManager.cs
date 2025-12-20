using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Wavee.Connect.Protocol;
using Wavee.Core.Http;
using Wavee.Core.Session;
using Wavee.Protocol.Player;

namespace Wavee.Connect;

/// <summary>
/// Manages Spotify Connect device state and coordinates state updates with Spotify's cloud API.
/// </summary>
/// <remarks>
/// This class subscribes to dealer connection ID updates and sends PUT state requests
/// to announce device presence, handle volume changes, and synchronize player state.
/// </remarks>
public sealed class DeviceStateManager : IAsyncDisposable
{
    private readonly ISession _session;
    private readonly SpClient _spClient;
    private readonly DealerClient _dealerClient;
    private readonly ILogger? _logger;

    // Subscriptions
    private readonly IDisposable _connectionIdSubscription;
    private readonly IDisposable _messageSubscription;

    // State
    private string? _connectionId;
    private DeviceInfo _deviceInfo;
    private int _currentVolume;
    private bool _isActive;
    private uint _messageId;

    // Volume observable
    private readonly BehaviorSubject<int> _volumeSubject;

    private bool _disposed;

    /// <summary>
    /// Observable stream of volume changes (0-65535 range).
    /// </summary>
    public IObservable<int> Volume => _volumeSubject.AsObservable();

    /// <summary>
    /// Gets the current volume (0-65535 range).
    /// </summary>
    public int CurrentVolume => _currentVolume;

    /// <summary>
    /// Gets whether the device is currently active in the Connect cluster.
    /// </summary>
    public bool IsActive => _isActive;

    /// <summary>
    /// Initializes a new instance of DeviceStateManager.
    /// </summary>
    /// <param name="session">Active Spotify session.</param>
    /// <param name="spClient">SpClient for making PUT state requests.</param>
    /// <param name="dealerClient">DealerClient for receiving connection ID and messages.</param>
    /// <param name="initialVolume">Initial volume (0-65535, default is half max).</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DeviceStateManager(
        ISession session,
        SpClient spClient,
        DealerClient dealerClient,
        int initialVolume = ConnectStateHelpers.MaxVolume / 2,
        ILogger? logger = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _spClient = spClient ?? throw new ArgumentNullException(nameof(spClient));
        _dealerClient = dealerClient ?? throw new ArgumentNullException(nameof(dealerClient));
        _logger = logger;

        // Initialize device info
        _currentVolume = Math.Clamp(initialVolume, 0, ConnectStateHelpers.MaxVolume);
        _deviceInfo = ConnectStateHelpers.CreateDeviceInfo(
            session.Config,
            _currentVolume);

        _volumeSubject = new BehaviorSubject<int>(_currentVolume);

        // Subscribe to connection ID updates (filter out null values from BehaviorSubject)
        _connectionIdSubscription = _dealerClient.ConnectionId
            .Where(id => id != null)
            .Subscribe(OnConnectionIdReceived);
        _logger?.LogTrace("Subscribed to DealerClient.ConnectionId observable (with null filter)");

        // Subscribe to volume change messages
        _messageSubscription = _dealerClient.Messages
            .Where(m => m.Uri.StartsWith("hm://connect-state/v1/connect/volume", StringComparison.OrdinalIgnoreCase))
            .Subscribe(OnVolumeMessage);
        _logger?.LogTrace("Subscribed to volume messages from DealerClient.Messages observable");

        _logger?.LogDebug("DeviceStateManager initialized for device {DeviceId}", session.Config.DeviceId);
    }

    /// <summary>
    /// Handles connection ID updates from dealer.
    /// </summary>
    private async void OnConnectionIdReceived(string? connectionId)
    {
        _logger?.LogTrace("OnConnectionIdReceived called: connectionId={ConnectionId}, isNull={IsNull}",
            connectionId ?? "<null>", connectionId == null);

        if (connectionId == null)
        {
            _logger?.LogDebug("Connection ID cleared");
            _connectionId = null;
            return;
        }

        _logger?.LogInformation("Connection ID received: {ConnectionId}", connectionId);
        _connectionId = connectionId;
        _logger?.LogTrace("Connection ID stored in _connectionId field: {ConnectionId}", _connectionId);

        // Announce device presence with NEW_CONNECTION reason
        try
        {
            await UpdateStateAsync(PutStateReason.NewConnection);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to announce device presence");
        }
    }

    /// <summary>
    /// Handles volume change messages from dealer.
    /// </summary>
    private async void OnVolumeMessage(DealerMessage message)
    {
        _logger?.LogTrace("OnVolumeMessage called: uri={Uri}, payloadSize={Size}",
            message.Uri, message.Payload.Length);

        try
        {
            // Parse SetVolumeCommand protobuf
            var volumeCommand = SetVolumeCommand.Parser.ParseFrom(message.Payload);
            var newVolume = (int)volumeCommand.Volume;

            _logger?.LogInformation("Volume changed to {Volume}", newVolume);
            _logger?.LogTrace("Updating device info volume: old={OldVolume}, new={NewVolume}",
                _deviceInfo.Volume, newVolume);

            // Update device info volume
            _deviceInfo.Volume = (uint)newVolume;
            _currentVolume = newVolume;

            _logger?.LogTrace("Device info volume updated, notifying subscribers");

            // Notify subscribers
            _volumeSubject.OnNext(newVolume);
            _logger?.LogTrace("Volume observable OnNext called with {Volume}", newVolume);

            // Update state with VOLUME_CHANGED reason
            await UpdateStateAsync(PutStateReason.VolumeChanged);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to handle volume message");
        }
    }

    /// <summary>
    /// Sends a PUT state request to Spotify's cloud API.
    /// </summary>
    /// <param name="reason">Reason for the state update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UpdateStateAsync(
        PutStateReason reason,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogTrace("UpdateStateAsync called: reason={Reason}, connectionId={ConnectionId}, isNull={IsNull}",
            reason, _connectionId ?? "<null>", _connectionId == null);

        if (_connectionId == null)
        {
            _logger?.LogWarning("Cannot update state: connection ID not available");
            return;
        }

        try
        {
            // Build PUT state request
            var request = new PutStateRequest
            {
                MemberType = MemberType.ConnectState,
                Device = new Device
                {
                    DeviceInfo = _deviceInfo,
                    PlayerState = ConnectStateHelpers.CreateEmptyPlayerState()
                },
                PutStateReason = reason,
                IsActive = _isActive,
                ClientSideTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MessageId = _messageId++
            };

            _logger?.LogDebug("Sending PUT state with reason {Reason}", reason);
            _logger?.LogTrace("PUT state request: deviceId={DeviceId}, connectionId={ConnectionId}, reason={Reason}, messageId={MessageId}, volume={Volume}, isActive={IsActive}",
                _session.Config.DeviceId, _connectionId, reason, request.MessageId, _deviceInfo.Volume, _isActive);

            var responseBody = await _spClient.PutConnectStateAsync(
                _session.Config.DeviceId,
                _connectionId,
                request,
                cancellationToken);

            _logger?.LogTrace("PUT state succeeded");

            // Process the response - Spotify returns a Cluster protobuf (not ClusterUpdate)
            if (responseBody.Length > 0)
            {
                _logger?.LogDebug("Processing PUT state response ({Size} bytes)", responseBody.Length);

                // Create a DealerMessage from the response to feed into PlaybackStateManager
                var dealerMessage = new DealerMessage
                {
                    Uri = "hm://connect-state/v1/put-state-response",
                    Headers = new Dictionary<string, string>(),
                    Payload = responseBody
                };

                // Submit to dealer's message worker for processing
                // This will trigger PlaybackStateManager's PUT state response handler
                await _dealerClient.SubmitMessageAsync(dealerMessage);

                _logger?.LogTrace("PUT state response processed");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update connect state with reason {Reason}", reason);
            throw;
        }
    }

    /// <summary>
    /// Sets the device active status and updates state.
    /// </summary>
    /// <param name="active">Whether the device should be active.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetActiveAsync(bool active, CancellationToken cancellationToken = default)
    {
        if (_isActive == active)
            return;

        _isActive = active;
        _logger?.LogInformation("Device active status changed to {Active}", active);

        var reason = active ? PutStateReason.NewDevice : PutStateReason.BecameInactive;
        await UpdateStateAsync(reason, cancellationToken);
    }

    /// <summary>
    /// Updates the device volume locally and sends state update.
    /// </summary>
    /// <param name="volume">New volume (0-65535).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken = default)
    {
        volume = Math.Clamp(volume, 0, ConnectStateHelpers.MaxVolume);

        if (_currentVolume == volume)
            return;

        _logger?.LogInformation("Setting volume to {Volume}", volume);

        _currentVolume = volume;
        _deviceInfo.Volume = (uint)volume;

        // Notify subscribers
        _volumeSubject.OnNext(volume);

        // Update state
        await UpdateStateAsync(PutStateReason.VolumeChanged, cancellationToken);
    }

    /// <summary>
    /// Disposes the device state manager and releases resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        _logger?.LogDebug("Disposing DeviceStateManager");

        // Unsubscribe from observables
        _connectionIdSubscription.Dispose();
        _messageSubscription.Dispose();

        // Complete volume subject
        _volumeSubject.OnCompleted();
        _volumeSubject.Dispose();

        // Optionally send inactive state before disposing
        if (_connectionId != null && _isActive)
        {
            try
            {
                await SetActiveAsync(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to set device inactive on dispose");
            }
        }

        _logger?.LogDebug("DeviceStateManager disposed");
    }
}
