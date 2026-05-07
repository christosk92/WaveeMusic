using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class PodcastBrowseViewModel : ObservableObject, IDisposable
{
    public void Dispose()
    {
        // TODO release managed resources here
    }

    public async Task LoadAsync(ContentNavigationParameter parameter)
    {

    }

    public string CurrentUri { get; set; }
    public string? SelectedHeroImageUrl { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public static string RootPodcastsUri { get; set; }
}
