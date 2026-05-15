using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Shared "Watch Video" affordance. Resolves the singleton
/// <see cref="PlayerBarViewModel"/> from <see cref="Ioc"/> at construction
/// so the XAML bindings light up without consumers having to thread the
/// VM through. Click invokes <c>SwitchToVideoCommand</c> — Mini-as-default
/// routing in the VM handles surfacing the floating player.
/// </summary>
public sealed partial class WatchVideoButton : UserControl
{
    public PlayerBarViewModel ViewModel { get; }

    public WatchVideoButton()
    {
        ViewModel = Ioc.Default.GetRequiredService<PlayerBarViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
    }

    public static readonly DependencyProperty ButtonSizeProperty =
        DependencyProperty.Register(
            nameof(ButtonSize), typeof(double), typeof(WatchVideoButton),
            new PropertyMetadata(32.0));

    /// <summary>Outer Button width/height in DIPs.</summary>
    public double ButtonSize
    {
        get => (double)GetValue(ButtonSizeProperty);
        set => SetValue(ButtonSizeProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize), typeof(double), typeof(WatchVideoButton),
            new PropertyMetadata(14.0));

    /// <summary>Inner glyph / spinner size in DIPs.</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SwitchToVideoCommand.CanExecute(null))
            ViewModel.SwitchToVideoCommand.Execute(null);
    }
}
