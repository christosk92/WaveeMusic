using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using CommunityToolkit.Mvvm.DependencyInjection;
using Wavee.UI.WinUI.Controls.Omnibar;
using Wavee.UI.WinUI.Data.Contracts;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls.NavigationToolbar;

public sealed partial class NavigationToolbar : UserControl
{
    private readonly IThemeService? _themeService;
    private readonly IAuthState _authState;

    public NavigationToolbar()
    {
        _themeService = Ioc.Default.GetService<IThemeService>();
        _authState = Ioc.Default.GetRequiredService<IAuthState>();
        InitializeComponent();
        ActualThemeChanged += OnActualThemeChanged;
        Unloaded += (_, _) => ActualThemeChanged -= OnActualThemeChanged;
        Loaded += OnFirstLoaded;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
        => UpdateThemeGlyph();

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded; // One-shot
        UpdateThemeGlyph();
    }

    private void UpdateThemeGlyph()
    {
        // Sun for light, moon for dark (shows current state)
        ThemeGlyph = ActualTheme == ElementTheme.Dark ? "\uE708" : "\uE706";
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

    public static readonly DependencyProperty IsRightPanelOpenProperty =
        DependencyProperty.Register(nameof(IsRightPanelOpen), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    /// <summary>
    /// Two-way bound to <c>ShellViewModel.IsRightPanelOpen</c>. The
    /// RightPanelToggleButton's IsChecked is bound to this DP, so toggling
    /// the button flips the panel and the button reflects the panel state
    /// (accent foreground when open via SubtleToggleButtonStyle).
    /// </summary>
    public bool IsRightPanelOpen
    {
        get => (bool)GetValue(IsRightPanelOpenProperty);
        set => SetValue(IsRightPanelOpenProperty, value);
    }

    public static readonly DependencyProperty IsFriendsPanelActiveProperty =
        DependencyProperty.Register(nameof(IsFriendsPanelActive), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool IsFriendsPanelActive
    {
        get => (bool)GetValue(IsFriendsPanelActiveProperty);
        set => SetValue(IsFriendsPanelActiveProperty, value);
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
            toolbar.SearchOmnibar.SearchResults = e.NewValue;
        }
    }

    public static readonly DependencyProperty SuggestionGroupsProperty =
        DependencyProperty.Register(nameof(SuggestionGroups), typeof(object), typeof(NavigationToolbar),
            new PropertyMetadata(null, OnSuggestionGroupsChanged));

    public object? SuggestionGroups
    {
        get => GetValue(SuggestionGroupsProperty);
        set => SetValue(SuggestionGroupsProperty, value);
    }

    private static void OnSuggestionGroupsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationToolbar toolbar)
        {
            toolbar.SearchOmnibar.SuggestionGroups = e.NewValue;
        }
    }

    public static readonly DependencyProperty SearchSuggestionsLoadingProperty =
        DependencyProperty.Register(nameof(SearchSuggestionsLoading), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false, OnSearchSuggestionsLoadingChanged));

    public bool SearchSuggestionsLoading
    {
        get => (bool)GetValue(SearchSuggestionsLoadingProperty);
        set => SetValue(SearchSuggestionsLoadingProperty, value);
    }

    private static void OnSearchSuggestionsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationToolbar toolbar)
        {
            toolbar.SearchOmnibar.IsLoading = (bool)e.NewValue;
        }
    }

    public static readonly DependencyProperty SearchSuggestionsErrorMessageProperty =
        DependencyProperty.Register(nameof(SearchSuggestionsErrorMessage), typeof(string), typeof(NavigationToolbar),
            new PropertyMetadata(null, OnSearchSuggestionsErrorMessageChanged));

    public string? SearchSuggestionsErrorMessage
    {
        get => (string?)GetValue(SearchSuggestionsErrorMessageProperty);
        set => SetValue(SearchSuggestionsErrorMessageProperty, value);
    }

    private static void OnSearchSuggestionsErrorMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationToolbar toolbar)
        {
            toolbar.SearchOmnibar.ErrorMessage = e.NewValue as string;
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

    public static readonly DependencyProperty IsConnectingProperty =
        DependencyProperty.Register(nameof(IsConnecting), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false, OnAuthVisualStateChanged));

    public bool IsConnecting
    {
        get => (bool)GetValue(IsConnectingProperty);
        set => SetValue(IsConnectingProperty, value);
    }

    public static readonly DependencyProperty IsOnHomePageProperty =
        DependencyProperty.Register(nameof(IsOnHomePage), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool IsOnHomePage
    {
        get => (bool)GetValue(IsOnHomePageProperty);
        set => SetValue(IsOnHomePageProperty, value);
    }

    public static readonly DependencyProperty ThemeGlyphProperty =
        DependencyProperty.Register(nameof(ThemeGlyph), typeof(string), typeof(NavigationToolbar),
            new PropertyMetadata("\uE708"));

    public string ThemeGlyph
    {
        get => (string)GetValue(ThemeGlyphProperty);
        set => SetValue(ThemeGlyphProperty, value);
    }

    public static readonly DependencyProperty IsOnProfilePageProperty =
        DependencyProperty.Register(nameof(IsOnProfilePage), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false));

    public bool IsOnProfilePage
    {
        get => (bool)GetValue(IsOnProfilePageProperty);
        set => SetValue(IsOnProfilePageProperty, value);
    }

    public static readonly DependencyProperty IsAuthenticatedProperty =
        DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(NavigationToolbar),
            new PropertyMetadata(false, OnAuthVisualStateChanged));

    public bool IsAuthenticated
    {
        get => (bool)GetValue(IsAuthenticatedProperty);
        set => SetValue(IsAuthenticatedProperty, value);
    }

    private static void OnAuthVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NavigationToolbar t)
        {
            t.UpdateAuthVisualState();
        }
    }

    public static readonly DependencyProperty ShowProfileChipProperty =
        DependencyProperty.Register(nameof(ShowProfileChip), typeof(Visibility), typeof(NavigationToolbar),
            new PropertyMetadata(Visibility.Collapsed));

    public Visibility ShowProfileChip
    {
        get => (Visibility)GetValue(ShowProfileChipProperty);
        private set => SetValue(ShowProfileChipProperty, value);
    }

    public static readonly DependencyProperty ShowSignInCtaProperty =
        DependencyProperty.Register(nameof(ShowSignInCta), typeof(Visibility), typeof(NavigationToolbar),
            new PropertyMetadata(Visibility.Visible));

    public Visibility ShowSignInCta
    {
        get => (Visibility)GetValue(ShowSignInCtaProperty);
        private set => SetValue(ShowSignInCtaProperty, value);
    }

    private void UpdateAuthVisualState()
    {
        // Profile chip shows for Authenticated and while connecting (so the spinner lands in-place).
        // Sign-in CTA shows only for a true signed-out state (neither authenticated nor transitioning).
        var authed = IsAuthenticated;
        var connecting = IsConnecting;
        ShowProfileChip = (authed || connecting) ? Visibility.Visible : Visibility.Collapsed;
        ShowSignInCta = (!authed && !connecting) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Suppresses the search flyout popup (used when already on SearchPage).
    /// </summary>
    public bool SuppressSearchFlyout
    {
        get => SearchOmnibar?.SuppressFlyout ?? false;
        set { if (SearchOmnibar != null) SearchOmnibar.SuppressFlyout = value; }
    }

    #endregion

    #region Events

    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? BackRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? ForwardRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? HomeRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? RefreshRequested;
    public event TypedEventHandler<NavigationToolbar, string>? SearchTextChanged;
    public event TypedEventHandler<NavigationToolbar, string>? SearchQuerySubmitted;
    public event TypedEventHandler<NavigationToolbar, object>? SearchSuggestionChosen;
    public event TypedEventHandler<NavigationToolbar, Data.Contracts.SearchSuggestionItem>? SearchActionButtonClicked;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? SearchRetryRequested;
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? FriendsRequested;

    /// <summary>
    /// Raised when the unauthenticated "Sign in" CTA is clicked. ShellPage listens and opens the sign-in dialog.
    /// Per-tab pages must not react — this is window-level UX.
    /// </summary>
    public event TypedEventHandler<NavigationToolbar, RoutedEventArgs>? SignInRequested;

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

    private void SearchOmnibar_SuggestionChosen(Omnibar.Omnibar sender, OmnibarSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem != null)
            SearchSuggestionChosen?.Invoke(this, args.SelectedItem);
    }

    private void SearchOmnibar_ActionButtonClicked(Omnibar.Omnibar sender, Data.Contracts.SearchSuggestionItem item)
    {
        SearchActionButtonClicked?.Invoke(this, item);
    }

    private void SearchOmnibar_RetryRequested(Omnibar.Omnibar sender, RoutedEventArgs args)
    {
        SearchRetryRequested?.Invoke(this, args);
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _themeService?.ToggleTheme();
    }

    private void FriendsButton_Click(object sender, RoutedEventArgs e)
    {
        FriendsRequested?.Invoke(this, e);
    }


    private void ViewProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenProfile(parameter: null, title: null, openInNewTab: openInNewTab);
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var openInNewTab = NavigationHelpers.IsCtrlPressed();
        NavigationHelpers.OpenSettings(openInNewTab);
    }

    private async void SignOutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _authState.LogoutAsync();
        }
        catch { /* best effort */ }
    }

    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        SignInRequested?.Invoke(this, e);
    }

    #endregion
}
