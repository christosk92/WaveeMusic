using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.WinUI.Data.Contexts;

namespace Wavee.UI.WinUI.Data.Contracts;

/// <summary>
/// Convenience facade exposing all app-wide state services.
/// Inject this when a component needs access to multiple state slices.
/// </summary>
public interface IAppState
{
    IAuthState Auth { get; }
    IPlaybackStateService Playback { get; }
    INotificationService Notifications { get; }
    IConnectivityService Connectivity { get; }
    IWindowContext Window { get; }
    IMessenger Messenger { get; }
}
