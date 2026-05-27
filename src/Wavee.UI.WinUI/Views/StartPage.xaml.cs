using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wavee.UI.WinUI.Controls.PageHost;
using Wavee.UI.WinUI.Helpers.Navigation;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

[global::WinRT.GeneratedBindableCustomProperty]
public sealed partial class StartPage : UserControl, IPageHostAware
{
    public StartPageViewModel ViewModel { get; } = new();

    public StartPage()
    {
        InitializeComponent();
    }

    public void OnEntered(object? parameter, PageHostNavigationMode mode) { }

    public void OnLeaving()
    {
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