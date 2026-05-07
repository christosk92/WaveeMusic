using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.SidebarPlayer;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Data.Enums;

namespace Wavee.UI.WinUI.Views;

/// <summary>
/// Now-playing page that hosts the same <see cref="ExpandedPlayerView"/> the
/// popout window (<c>PlayerFloatingWindow</c>) uses. Owns surface lifecycle
/// and Mode persistence; everything else is the inner control's responsibility.
///
/// Mirrors the popout's hosting pattern, with two surface-ownership tweaks for
/// in-tab context:
///   - <see cref="ExpandedPlayerView.SetCanTakeVideoSurfaceFromVideoPage"/> stays
///     <c>false</c> so the popout still wins when both are open.
///   - Mini-player suppression flag is not toggled here; <c>ShellViewModel</c>
///     already calls <c>MiniVideoPlayerViewModel.SetOnVideoPage</c> on Frame
///     navigation events.
/// </summary>
public sealed partial class VideoPlayerPage : Page
{
    private readonly IShellSessionService _shellSession;
    private long _expandedModeCallbackToken = -1;

    public VideoPlayerPage()
    {
        InitializeComponent();
        _shellSession = Ioc.Default.GetRequiredService<IShellSessionService>();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Restore the user's last-selected right-panel mode, shared with the
        // popout window via the same shell-layout key.
        var layout = _shellSession.GetLayoutSnapshot();
        var mode = ExpandedPlayerContentMode.Lyrics;
        if (Enum.TryParse<ExpandedPlayerContentMode>(layout.PlayerWindowExpandedMode, out var parsed)
            && parsed != ExpandedPlayerContentMode.None)
        {
            mode = parsed;
        }
        ExpandedHost.Mode = mode;
        _shellSession.UpdateLayout(s => s.PlayerWindowExpandedMode = mode.ToString());

        _expandedModeCallbackToken = ExpandedHost.RegisterPropertyChangedCallback(
            ExpandedPlayerView.ModeProperty,
            OnExpandedModeChanged);

        ExpandedHost.SetVideoSurfaceEnabled(true);
        ExpandedHost.SetCanTakeVideoSurfaceFromVideoPage(false);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        if (_expandedModeCallbackToken >= 0)
        {
            ExpandedHost.UnregisterPropertyChangedCallback(
                ExpandedPlayerView.ModeProperty,
                _expandedModeCallbackToken);
            _expandedModeCallbackToken = -1;
        }

        ExpandedHost.SetVideoSurfaceEnabled(false);
        ExpandedHost.ReleaseHeavyResources();
    }

    private void OnExpandedModeChanged(DependencyObject sender, DependencyProperty dp)
    {
        var modeName = ExpandedHost.Mode.ToString();
        _shellSession.UpdateLayout(s => s.PlayerWindowExpandedMode = modeName);
    }
}
