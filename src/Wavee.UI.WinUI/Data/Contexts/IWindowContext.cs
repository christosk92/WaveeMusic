using System.ComponentModel;

namespace Wavee.UI.WinUI.Data.Contexts;

public interface IWindowContext : INotifyPropertyChanged
{
    bool IsCompactOverlay { get; }
    bool IsFullScreen { get; }
    bool IsRunningAsAdmin { get; }
}
