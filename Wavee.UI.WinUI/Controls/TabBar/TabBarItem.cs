using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Wavee.UI.WinUI.Data.Parameters;

namespace Wavee.UI.WinUI.Controls.TabBar;

public sealed partial class TabBarItem : ObservableObject, ITabBarItem, IDisposable
{
    public Frame ContentFrame { get; }

    public event EventHandler<Microsoft.UI.Xaml.Navigation.NavigationEventArgs>? Navigated;
    public event EventHandler<TabItemParameter>? ContentChanged;

    [ObservableProperty]
    private IconSource? _iconSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    private string? _header;

    [ObservableProperty]
    private string? _toolTipText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(PinIndicatorVisibility))]
    [NotifyPropertyChangedFor(nameof(CompactWidth))]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayHeader))]
    [NotifyPropertyChangedFor(nameof(TabStyle))]
    [NotifyPropertyChangedFor(nameof(CompactWidth))]
    private bool _isCompact;

    /// <summary>
    /// Returns empty string when compact (icon-only), otherwise the header text
    /// </summary>
    public string? DisplayHeader => IsCompact ? "" : Header;

    /// <summary>
    /// Returns the appropriate style based on IsCompact state
    /// </summary>
    public Style? TabStyle => IsCompact
        ? Application.Current.Resources["TabBarItemCompactStyle"] as Style
        : Application.Current.Resources["TabBarItemStyle"] as Style;

    /// <summary>
    /// Returns a narrow max width when compact, otherwise no limit.
    /// Pinned compact tabs are slightly wider to show the pin indicator.
    /// </summary>
    public double CompactWidth => IsCompact ? (IsPinned ? 64 : 44) : double.PositiveInfinity;

    /// <summary>
    /// Returns Visible when pinned, Collapsed otherwise (for pin badge)
    /// </summary>
    public Visibility PinIndicatorVisibility => IsPinned ? Visibility.Visible : Visibility.Collapsed;

    private ITabBarItemContent? _previousContent;
    private const int MaxBackStackSize = 20;

    private TabItemParameter? _navigationParameter;
    public TabItemParameter? NavigationParameter
    {
        get => _navigationParameter;
        set
        {
            if (value != _navigationParameter)
            {
                _navigationParameter = value;
                if (_navigationParameter?.InitialPageType != null)
                {
                    ContentFrame.Navigate(
                        _navigationParameter.InitialPageType,
                        _navigationParameter.NavigationParameter,
                        new DrillInNavigationTransitionInfo());
                }
                else
                {
                    ContentFrame.Content = null;
                }
            }
        }
    }

    public ITabBarItemContent? TabItemContent => ContentFrame.Content as ITabBarItemContent;

    public TabBarItem()
    {
        ContentFrame = new Frame
        {
            CacheSize = 5,
            IsNavigationStackEnabled = true
        };
        ContentFrame.Navigated += ContentFrame_Navigated;
        ContentFrame.NavigationFailed += (_, e) =>
        {
            e.Handled = true;
            System.Diagnostics.Debug.WriteLine(
                $"NavigationFailed [{e.SourcePageType?.Name}]: {e.Exception?.Message}");
        };
    }

    public void Navigate(Type pageType, object? parameter = null, bool suppressTransition = false)
    {
        _navigationParameter = new TabItemParameter
        {
            InitialPageType = pageType,
            NavigationParameter = parameter
        };

        var transition = suppressTransition
            ? (NavigationTransitionInfo)new SuppressNavigationTransitionInfo()
            : new DrillInNavigationTransitionInfo();
        ContentFrame.Navigate(pageType, parameter, transition);
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        // Forward navigation event for external subscribers
        Navigated?.Invoke(this, e);

        // Unsubscribe from previous page's ContentChanged to prevent leak
        if (_previousContent != null)
            _previousContent.ContentChanged -= TabItemContent_ContentChanged;

        _previousContent = TabItemContent;

        if (TabItemContent != null)
            TabItemContent.ContentChanged += TabItemContent_ContentChanged;

        // Cap BackStack to prevent unbounded growth
        while (ContentFrame.BackStack.Count > MaxBackStackSize)
            ContentFrame.BackStack.RemoveAt(0);
    }

    private void TabItemContent_ContentChanged(object? sender, TabItemParameter e)
    {
        _navigationParameter = e;
        ContentChanged?.Invoke(this, e);
    }

    public void Dispose()
    {
        ContentFrame.Navigated -= ContentFrame_Navigated;
        ContentFrame.BackStack.Clear();

        if (TabItemContent is IDisposable disposable)
        {
            disposable.Dispose();
        }
        ContentFrame.Content = null;
    }
}
