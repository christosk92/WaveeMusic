using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Wavee.UI.WinUI.Controls.Omnibar;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.NavigationToolbar;

public sealed partial class NavigationToolbar : UserControl
{
    public NavigationToolbar()
    {
        InitializeComponent();
    }

    #region Dependency Properties

    public static readonly DependencyProperty CanGoBackProperty =
        DependencyProperty.Register(nameof(CanGoBack), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool CanGoBack
    {
        get => (bool)GetValue(CanGoBackProperty);
        set => SetValue(CanGoBackProperty, value);
    }

    public static readonly DependencyProperty CanGoForwardProperty =
        DependencyProperty.Register(nameof(CanGoForward), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool CanGoForward
    {
        get => (bool)GetValue(CanGoForwardProperty);
        set => SetValue(CanGoForwardProperty, value);
    }

    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(NavigationToolbar),
            new PropertyMetadata(string.Empty));

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public static readonly DependencyProperty SearchSuggestionsProperty =
        DependencyProperty.Register(nameof(SearchSuggestions), typeof(object), typeof(NavigationToolbar),
            new PropertyMetadata(null, OnSearchSuggestionsChanged));

    public object? SearchSuggestions
    {
        get => GetValue(SearchSuggestionsProperty);
        set => SetValue(SearchSuggestionsProperty, value);
    }

    private static void OnSearchSuggestionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationToolbar toolbar)
        {
            toolbar.SearchOmnibar.ItemsSource = e.NewValue;
        }
    }

    public static readonly DependencyProperty UserDisplayNameProperty =
        DependencyProperty.Register(nameof(UserDisplayName), typeof(string), typeof(NavigationToolbar),
            new PropertyMetadata("User"));

    public string UserDisplayName
    {
        get => (string)GetValue(UserDisplayNameProperty);
        set => SetValue(UserDisplayNameProperty, value);
    }

    public static readonly DependencyProperty IsOnHomePageProperty =
        DependencyProperty.Register(nameof(IsOnHomePage), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool IsOnHomePage
    {
        get => (bool)GetValue(IsOnHomePageProperty);
        set => SetValue(IsOnHomePageProperty, value);
    }

    public static readonly DependencyProperty IsOnProfilePageProperty =
        DependencyProperty.Register(nameof(IsOnProfilePage), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool IsOnProfilePage
    {
        get => (bool)GetValue(IsOnProfilePageProperty);
        set => SetValue(IsOnProfilePageProperty, value);
    }

    #endregion

    #region Events

    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? BackRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? ForwardRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? HomeRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? RefreshRequested;
    public event TypedEventHandler<NavigationToolbar, string>? SearchTextChanged;
    public event TypedEventHandler<NavigationToolbar, string>? SearchQuerySubmitted;

    #endregion

    #region Event Handlers

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, e);
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        ForwardRequested?.Invoke(this, e);
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e)
    {
        HomeRequested?.Invoke(this, e);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, e);
    }

    private void SearchOmnibar_TextChanged(Omnibar.Omnibar sender, OmnibarTextChangedEventArgs args)
    {
        SearchText = args.Text;
        SearchTextChanged?.Invoke(this, args.Text);
    }

    private void SearchOmnibar_QuerySubmitted(Omnibar.Omnibar sender, OmnibarQuerySubmittedEventArgs args)
    {
        SearchQuerySubmitted?.Invoke(this, args.QueryText);
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenProfile(openInNewTab);
    }

    #endregion
}
