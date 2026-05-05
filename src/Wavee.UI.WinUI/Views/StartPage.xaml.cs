using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class StartPage : Page
{
    public StartPageViewModel ViewModel { get; } = new();

    public StartPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not pin this page across navigations.
        Bindings?.StopTracking();
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            NavigationHelpers.OpenSearch(args.QueryText);
        }
    }

    private void QuickAccess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is QuickAccessItem item)
        {
            item.Action?.Invoke();
        }
    }
}
