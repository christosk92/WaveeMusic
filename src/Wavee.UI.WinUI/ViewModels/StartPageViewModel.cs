using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.ViewModels;

public sealed partial class StartPageViewModel : ObservableObject
{
    public ObservableCollection<QuickAccessItem> QuickAccessItems { get; } = [];

    public StartPageViewModel()
    {
        LoadQuickAccess();
    }

    private void LoadQuickAccess()
    {
        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Home",
            Glyph = "\uE80F",
            Action = () => NavigationHelpers.OpenHome()
        });

        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Search",
            Glyph = "\uE721",
            Action = () => NavigationHelpers.OpenSearch()
        });

        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Library",
            Glyph = "\uE8F1",
            Action = () => NavigationHelpers.OpenLibrary()
        });

        QuickAccessItems.Add(new QuickAccessItem
        {
            Title = "Liked Songs",
            Glyph = "\uEB52",
            Action = () => NavigationHelpers.OpenLikedSongs()
        });
    }

    [RelayCommand]
    private void Search(string query)
    {
        if (!string.IsNullOrWhiteSpace(query))
        {
            NavigationHelpers.OpenSearch(query);
        }
    }
}

public class QuickAccessItem
{
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required Action Action { get; init; }
}
