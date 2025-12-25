using System;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Data.Contracts;

public interface INavigationService
{
    bool CanGoBack { get; }

    void Initialize(Frame frame);
    void GoBack();
    void NavigateTo(Type pageType, object? parameter = null);
    void NavigateTo<TPage>(object? parameter = null) where TPage : Page;
}
