using CommunityToolkit.Mvvm.Messaging;
using Wavee.UI.Contracts;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Data.Contexts;

/// <summary>
/// Convenience facade that exposes all app-wide state services.
/// </summary>
internal sealed class AppState : IAppState
{
    public IAuthState Auth { get; }
    public IPlaybackStateService Playback { get; }
    public INotificationService Notifications { get; }
    public IConnectivityService Connectivity { get; }
    public IWindowContext Window { get; }
    public IMessenger Messenger { get; }

    public AppState(
        IAuthState auth,
        IPlaybackStateService playback,
        INotificationService notifications,
        IConnectivityService connectivity,
        IWindowContext window,
        IMessenger messenger)
    {
        Auth = auth;
        Playback = playback;
        Notifications = notifications;
        Connectivity = connectivity;
        Window = window;
        Messenger = messenger;
    }
}
