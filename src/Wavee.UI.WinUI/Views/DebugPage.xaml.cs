using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;
using Wavee.UI.WinUI.Controls.TabBar;
using Wavee.UI.WinUI.Data.Parameters;
using Wavee.UI.WinUI.ViewModels;

namespace Wavee.UI.WinUI.Views;

public sealed partial class DebugPage : Page, ITabBarItemContent
{
    public DebugViewModel ViewModel { get; }
    public TabItemParameter? TabItemParameter => null;
    public event EventHandler<TabItemParameter>? ContentChanged;

    public DebugPage()
    {
        ViewModel = Ioc.Default.GetRequiredService<DebugViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // Detach compiled x:Bind from VM.PropertyChanged so the BindingsTracking
        // sibling does not pin this page across navigations.
        Bindings?.StopTracking();
    }
}

/// <summary>
/// Converts a selected index to Visibility. Shows when index matches ConverterParameter.
/// </summary>
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int index && parameter is string paramStr && int.TryParse(paramStr, out var target))
            return index == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// Negates a boolean value.
/// </summary>
public sealed class BoolNegationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : value;
}
