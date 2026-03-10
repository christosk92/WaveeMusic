using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Messages;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Tracks network/backend connectivity status. Initial implementation starts
/// as connected; will be integrated with <see cref="Core.Session.ISession"/>
/// connection events in the future.
/// </summary>
internal sealed partial class ConnectivityService : ObservableObject, IConnectivityService
{
    private readonly IMessenger _messenger;

    [ObservableProperty]
    private bool _isConnected = true;

    [ObservableProperty]
    private bool _isReconnecting;

    [ObservableProperty]
    private DateTimeOffset? _lastConnectedAt = DateTimeOffset.UtcNow;

    public ConnectivityService(IMessenger messenger)
    {
        _messenger = messenger;
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
}
