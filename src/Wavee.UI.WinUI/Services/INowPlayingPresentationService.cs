using System.ComponentModel;

namespace Wavee.UI.WinUI.Services;

/// <summary>
/// How the now-playing surface is presented in the shell. Two independent modes
/// rather than nested levels: a user can jump straight from Normal to Fullscreen
/// without going through Theatre, and vice-versa.
/// </summary>
public enum NowPlayingPresentation
{
    /// <summary>Default — bottom-docked PlayerBar; pages own the centre content area.</summary>
    Normal,

    /// <summary>
    /// Player takes over the whole content area (sidebar, tab strip, nav toolbar,
    /// and bottom PlayerBar all hidden). The custom title bar stays so window
    /// controls remain reachable.
    /// </summary>
    Theatre,

    /// <summary>
    /// Same surface as Theatre plus the AppWindow flipped to
    /// <see cref="Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen"/> —
    /// taskbar gone, title bar gone, player chrome auto-hides on mouse idle.
    /// </summary>
    Fullscreen,
}

/// <summary>
/// Owns the global presentation mode for the now-playing surface. Resolved as
/// a singleton from DI; <see cref="MainWindow"/> swaps the AppWindow presenter
/// on transitions, <see cref="ViewModels.ShellViewModel"/> hides chrome based
/// on the current value, and <see cref="ViewModels.PlayerBarViewModel"/> binds
/// its expand / fullscreen commands here.
/// </summary>
public interface INowPlayingPresentationService : INotifyPropertyChanged
{
    /// <summary>Current presentation.</summary>
    NowPlayingPresentation Presentation { get; }

    /// <summary>True when <see cref="Presentation"/> is Theatre or Fullscreen.</summary>
    bool IsExpanded { get; }

    /// <summary>True only when <see cref="Presentation"/> is Normal.</summary>
    bool IsNormal { get; }

    /// <summary>Switch into Theatre. From Fullscreen, drops the OS-level fullscreen too.</summary>
    void EnterTheatre();

    /// <summary>Switch into Fullscreen (Theatre layout + OS fullscreen presenter).</summary>
    void EnterFullscreen();

    /// <summary>Return to Normal.</summary>
    void ExitToNormal();

    /// <summary>F11 / dedicated button — Normal ↔ Fullscreen.</summary>
    void ToggleFullscreen();

    /// <summary>Theatre-button click — Normal ↔ Theatre.</summary>
    void ToggleTheatre();
}
