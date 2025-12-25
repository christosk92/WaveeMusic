using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PlaylistPage : Page
{
    public string? PlaylistId { get; private set; }

    public PlaylistPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string playlistId && !string.IsNullOrWhiteSpace(playlistId))
        {
            PlaylistId = playlistId;
            // Future: Load playlist data
        }
    }
}
