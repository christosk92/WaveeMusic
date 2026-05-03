using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Enums;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.Views;

public sealed partial class PodcastBrowsePage : Page, ITabBarItemContent
{
    private TabItemParameter? _tabItemParameter;

    public TabItemParameter? TabItemParameter => _tabItemParameter;
    public event EventHandler<TabItemParameter>? ContentChanged;

    public PodcastBrowsePage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ApplyParameter(e.Parameter as ContentNavigationParameter);
    }

    private void ApplyParameter(ContentNavigationParameter? parameter)
    {
        var title = string.IsNullOrWhiteSpace(parameter?.Title)
            ? "Podcasts"
            : parameter!.Title!;
        var uri = parameter?.Uri ?? "";

        SubtitleText.Text = string.Equals(title, "Podcasts", StringComparison.Ordinal)
            ? "Browse podcasts by topic."
            : $"Browse podcasts in {title}.";

        TopicTokenView.ItemsSource = new[] { title };
        TopicTokenView.SelectedIndex = 0;
        UriText.Text = uri;

        _tabItemParameter = new TabItemParameter(NavigationPageType.PodcastBrowse, parameter)
        {
            Title = title
        };
        ContentChanged?.Invoke(this, _tabItemParameter);
    }
}
