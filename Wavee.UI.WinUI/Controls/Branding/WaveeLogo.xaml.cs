using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Branding;

public sealed partial class WaveeLogo : UserControl
{
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(WaveeLogo),
        new PropertyMetadata(null));

    public WaveeLogo()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Brush used to fill the mark. Defaults to the user's system accent
    /// (Settings → Personalization → Colors). Override from XAML to pin to
    /// any colour — brand green, an artist palette, etc.
    /// </summary>
    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pull the system accent brush at load time if no caller-supplied
        // override is set. Resolved from app resources so it picks up the
        // platform-provided ThemeResource that updates when the user
        // changes their accent in Settings.
        if (AccentBrush is null && Application.Current.Resources.TryGetValue("SystemAccentColorBrush", out var resource)
            && resource is Brush systemBrush)
        {
            AccentBrush = systemBrush;
        }
    }
}
