using System;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Reusable error-state surface for full-page failures: an icon, a title,
/// an optional explanatory message, and a Retry button bound to whatever
/// callback the host page supplies. Pages bind their <c>HasError</c> /
/// <c>ErrorMessage</c> / a Retry command and host this control where their
/// content would otherwise render. Empty / loading states are intentionally
/// out of scope here — those each warrant their own control because their
/// visuals diverge per page.
/// </summary>
public sealed partial class PageErrorState : UserControl
{
    public PageErrorState()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageErrorState),
            new PropertyMetadata("Something went wrong"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(PageErrorState),
            new PropertyMetadata(string.Empty, (d, _) => ((PageErrorState)d).UpdateHasMessage()));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty HasMessageProperty =
        DependencyProperty.Register(nameof(HasMessage), typeof(Visibility), typeof(PageErrorState),
            new PropertyMetadata(Visibility.Collapsed));

    public Visibility HasMessage
    {
        get => (Visibility)GetValue(HasMessageProperty);
        private set => SetValue(HasMessageProperty, value);
    }

    public static readonly DependencyProperty RetryCommandProperty =
        DependencyProperty.Register(nameof(RetryCommand), typeof(ICommand), typeof(PageErrorState),
            new PropertyMetadata(null, (d, _) => ((PageErrorState)d).UpdateShowRetry()));

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public static readonly DependencyProperty RetryCommandParameterProperty =
        DependencyProperty.Register(nameof(RetryCommandParameter), typeof(object), typeof(PageErrorState),
            new PropertyMetadata(null));

    public object? RetryCommandParameter
    {
        get => GetValue(RetryCommandParameterProperty);
        set => SetValue(RetryCommandParameterProperty, value);
    }

    public static readonly DependencyProperty ShowRetryProperty =
        DependencyProperty.Register(nameof(ShowRetry), typeof(Visibility), typeof(PageErrorState),
            new PropertyMetadata(Visibility.Collapsed));

    public Visibility ShowRetry
    {
        get => (Visibility)GetValue(ShowRetryProperty);
        private set => SetValue(ShowRetryProperty, value);
    }

    public event EventHandler? RetryRequested;

    private void UpdateHasMessage()
        => HasMessage = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;

    private void UpdateShowRetry()
        => ShowRetry = RetryCommand is not null ? Visibility.Visible : Visibility.Collapsed;

    private void OnRetryClicked(object sender, RoutedEventArgs e)
    {
        if (RetryCommand is { } cmd && cmd.CanExecute(RetryCommandParameter))
            cmd.Execute(RetryCommandParameter);
        RetryRequested?.Invoke(this, EventArgs.Empty);
    }
}
