using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Wavee.UI.WinUI.Views;

public sealed partial class SearchPage : Page
{
    public string? Query { get; private set; }

    public SearchPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            Query = query;
            SearchQueryText.Text = $"Search results for \"{query}\" coming soon...";
        }
    }
}
