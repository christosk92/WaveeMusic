using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Wavee.UI.WinUI.Data.Contracts;

namespace Wavee.UI.WinUI.Services.Docking;

/// <summary>
/// Owns runtime tear-off state for shell panels. Single source of truth: which
/// panels are floating, which window hosts each one, and persistence of their
/// geometry across launches. Bound from XAML via
/// <see cref="ViewModels.ShellViewModel.Docking"/>.
/// </summary>
public interface IPanelDockingService : INotifyPropertyChanged
{
    /// <summary>True when the sidebar player widget is hosted in a floating window.</summary>
    bool IsPlayerDetached { get; }

    /// <summary>True when the right panel is hosted in a floating window.</summary>
    bool IsRightPanelDetached { get; }

    /// <summary>The floating <see cref="AppWindow"/> hosting the panel, or null when docked.</summary>
    AppWindow? GetWindowFor(DetachablePanel panel);

    /// <summary>
    /// Tear the panel into its own window. No-op if already detached. <paramref name="spawnAt"/>
    /// is in screen coordinates; when null the window centers on the panel's last position
    /// (or the main window when no last position is recorded).
    /// </summary>
    void Detach(DetachablePanel panel, PointInt32? spawnAt = null);

    /// <summary>Bring the panel back into the main window. No-op if already docked.</summary>
    void Dock(DetachablePanel panel);

    /// <summary>
    /// Called by the floating window from <c>AppWindow.Closing</c>. Cancels the close
    /// and re-docks the panel. Always X = re-dock per the design.
    /// </summary>
    void HandleFloatingClose(DetachablePanel panel);

    /// <summary>
    /// Called by the floating window when geometry changes (move/resize). The service
    /// persists the new rect via <see cref="IShellSessionService"/>.
    /// </summary>
    void NotifyFloatingGeometryChanged(DetachablePanel panel);

    /// <summary>
    /// Restore detached panels recorded in persisted state. Call once after the main window
    /// is shown — earlier and the new windows have no parent monitor to clamp against.
    /// </summary>
    Task RehydrateAsync();
}
