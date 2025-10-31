using Google.Protobuf;
using Wavee.Connect;
using Wavee.Connect.Protocol;
using Wavee.Core.Http;
using Wavee.Protocol.Player;

namespace Wavee.Tests.Helpers;

/// <summary>
/// Helper methods for creating test objects for DeviceStateManager tests.
/// </summary>
internal static class DeviceStateTestHelpers
{
    /// <summary>
    /// Creates a SetVolumeCommand protobuf message.
    /// </summary>
    /// <param name="volume">Volume value (0-65535).</param>
    /// <returns>SetVolumeCommand protobuf message.</returns>
    public static SetVolumeCommand CreateSetVolumeCommand(int volume)
    {
        return new SetVolumeCommand
        {
            Volume = Math.Clamp(volume, 0, ConnectStateHelpers.MaxVolume)
        };
    }

    /// <summary>
    /// Creates a DealerMessage for volume change.
    /// </summary>
    /// <param name="volume">Volume value (0-65535).</param>
    /// <returns>DealerMessage with volume command.</returns>
    public static DealerMessage CreateVolumeMessage(int volume)
    {
        var command = CreateSetVolumeCommand(volume);
        var payload = command.ToByteArray();

        return new DealerMessage
        {
            Uri = "hm://connect-state/v1/connect/volume",
            Headers = new Dictionary<string, string>(),
            Payload = payload
        };
    }

    /// <summary>
    /// Creates a DealerMessage for connection ID announcement.
    /// </summary>
    /// <param name="connectionId">Connection ID string.</param>
    /// <returns>DealerMessage with connection ID.</returns>
    public static DealerMessage CreateConnectionIdMessage(string connectionId)
    {
        var headers = new Dictionary<string, string>
        {
            ["Spotify-Connection-Id"] = connectionId
        };

        return new DealerMessage
        {
            Uri = "hm://pusher/v1/connections/",
            Headers = headers,
            Payload = Array.Empty<byte>()
        };
    }

    /// <summary>
    /// Creates a test DeviceStateManager with mock dependencies.
    /// </summary>
    /// <param name="spClient">Optional SpClient to use (creates mock if null).</param>
    /// <param name="dealerClient">Optional DealerClient to use (creates mock if null).</param>
    /// <param name="initialVolume">Initial volume (default is mid-range).</param>
    /// <returns>Configured DeviceStateManager instance.</returns>
    public static DeviceStateManager CreateDeviceStateManager(
        SpClient? spClient = null,
        DealerClient? dealerClient = null,
        int initialVolume = ConnectStateHelpers.MaxVolume / 2)
    {
        var session = DealerTestHelpers.CreateMockSession();
        spClient ??= MockSpClientHelpers.CreateMockSpClient();
        dealerClient ??= CreateMockDealerClient();

        return new DeviceStateManager(
            session,
            spClient,
            dealerClient,
            initialVolume);
    }

    /// <summary>
    /// Creates a mock DealerClient for testing.
    /// </summary>
    /// <returns>Mock DealerClient instance.</returns>
    public static DealerClient CreateMockDealerClient()
    {
        // Create a minimal DealerClient for testing
        // It won't be connected, but observables will work
        return new DealerClient();
    }

    /// <summary>
    /// Creates a mock DealerClient with a MockDealerConnection for testing.
    /// Allows simulating connection ID messages.
    /// </summary>
    /// <returns>Tuple of (DealerClient, MockDealerConnection).</returns>
    public static (DealerClient client, MockDealerConnection connection) CreateMockDealerClientWithConnection()
    {
        var mockConnection = new MockDealerConnection();
        var dealerClient = new DealerClient(connection: mockConnection);
        return (dealerClient, mockConnection);
    }

    /// <summary>
    /// Creates a test PutStateRequest.
    /// </summary>
    /// <param name="deviceInfo">Optional DeviceInfo.</param>
    /// <param name="isActive">Whether device is active.</param>
    /// <param name="reason">Reason for state update.</param>
    /// <param name="messageId">Message ID.</param>
    /// <returns>Configured PutStateRequest.</returns>
    public static PutStateRequest CreatePutStateRequest(
        DeviceInfo? deviceInfo = null,
        bool isActive = true,
        PutStateReason reason = PutStateReason.NewConnection,
        uint messageId = 1)
    {
        deviceInfo ??= CreateTestDeviceInfo();

        return new PutStateRequest
        {
            MemberType = MemberType.ConnectState,
            Device = new Device
            {
                DeviceInfo = deviceInfo,
                PlayerState = ConnectStateHelpers.CreateEmptyPlayerState()
            },
            PutStateReason = reason,
            IsActive = isActive,
            ClientSideTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageId = messageId
        };
    }

    /// <summary>
    /// Creates a test DeviceInfo.
    /// </summary>
    /// <param name="deviceId">Optional device ID.</param>
    /// <param name="deviceName">Optional device name.</param>
    /// <param name="volume">Volume (0-65535).</param>
    /// <returns>Configured DeviceInfo.</returns>
    public static DeviceInfo CreateTestDeviceInfo(
        string? deviceId = null,
        string? deviceName = null,
        int volume = ConnectStateHelpers.MaxVolume / 2)
    {
        var session = DealerTestHelpers.CreateMockSession();
        return ConnectStateHelpers.CreateDeviceInfo(session.Config, volume);
    }

    /// <summary>
    /// Waits for an observable to emit a specific value or timeout.
    /// </summary>
    /// <typeparam name="T">Observable value type.</typeparam>
    /// <param name="observable">Observable to wait on.</param>
    /// <param name="predicate">Predicate to match value.</param>
    /// <param name="timeout">Timeout duration.</param>
    /// <returns>Task that completes when value is emitted or timeout occurs.</returns>
    public static async Task<T> WaitForObservableAsync<T>(
        IObservable<T> observable,
        Func<T, bool> predicate,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);

        var tcs = new TaskCompletionSource<T>();
        var cts = new CancellationTokenSource(timeout.Value);

        cts.Token.Register(() => tcs.TrySetCanceled());

        var subscription = observable.Subscribe(
            onNext: value =>
            {
                if (predicate(value))
                {
                    tcs.TrySetResult(value);
                }
            },
            onError: ex => tcs.TrySetException(ex));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            subscription.Dispose();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Waits for an observable to emit any value or timeout.
    /// </summary>
    /// <typeparam name="T">Observable value type.</typeparam>
    /// <param name="observable">Observable to wait on.</param>
    /// <param name="timeout">Timeout duration.</param>
    /// <returns>Task that completes when value is emitted or timeout occurs.</returns>
    public static Task<T> WaitForObservableAsync<T>(
        IObservable<T> observable,
        TimeSpan? timeout = null)
    {
        return WaitForObservableAsync(observable, _ => true, timeout);
    }
}
