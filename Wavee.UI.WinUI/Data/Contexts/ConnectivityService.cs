using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Dispatching;
using Wavee.Core.Session;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Tracks network/backend connectivity status by subscribing to
/// <see cref="ISession.ConnectionState"/>.
/// </summary>
internal sealed partial class ConnectivityService : ObservableObject, IConnectivityService, IDisposable
{
    private readonly IMessenger _messenger;
    private readonly DispatcherQueue? _dispatcher;
    private IDisposable? _subscription;

    [ObservableProperty]
    private bool _isConnected = true;

    [ObservableProperty]
    private bool _isReconnecting;

    [ObservableProperty]
    private DateTimeOffset? _lastConnectedAt = DateTimeOffset.UtcNow;

    public ConnectivityService(IMessenger messenger)
    {
        _messenger = messenger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Subscribes to the session's connection state observable.
    /// Call once after the session is created.
    /// </summary>
    public void SubscribeToSession(IObservable<SessionConnectionState> connectionState)
    {
        _subscription?.Dispose();
        _subscription = connectionState.Subscribe(OnConnectionStateChanged);
    }

    private void OnConnectionStateChanged(SessionConnectionState state)
    {
        void Apply()
        {
            switch (state)
            {
                case SessionConnectionState.Connected:
                    IsConnected = true;
                    IsReconnecting = false;
                    break;
                case SessionConnectionState.Reconnecting:
                    IsConnected = false;
                    IsReconnecting = true;
                    break;
                case SessionConnectionState.Disconnected:
                    IsConnected = false;
                    IsReconnecting = false;
                    break;
            }
        }

        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            _dispatcher.TryEnqueue(Apply);
        else
            Apply();
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (value)
        {
            LastConnectedAt = DateTimeOffset.UtcNow;
            IsReconnecting = false;
        }

        _messenger.Send(new ConnectivityChangedMessage(value));
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
