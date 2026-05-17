using System;
using System.ComponentModel;

namespace Wavee.UI.Contracts;

/// <summary>
/// Tracks network/backend connectivity status.
/// </summary>
public interface IConnectivityService : INotifyPropertyChanged
{
    bool IsConnected { get; }
    bool IsReconnecting { get; }
    DateTimeOffset? LastConnectedAt { get; }
}