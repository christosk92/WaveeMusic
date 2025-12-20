using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.WinUI.Views.Controls;

public sealed partial class ErrorStateView : UserControl
{
    public ErrorStateView()
    {
        InitializeComponent();
    }

    // Title Property
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(ErrorStateView),
            new PropertyMetadata("An Error Occurred"));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    // Message Property
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(ErrorStateView),
            new PropertyMetadata("Something went wrong. Please try again."));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    // Details Property
    public static readonly DependencyProperty DetailsProperty =
        DependencyProperty.Register(
            nameof(Details),
            typeof(string),
            typeof(ErrorStateView),
            new PropertyMetadata(null));

    public string? Details
    {
        get => (string?)GetValue(DetailsProperty);
        set => SetValue(DetailsProperty, value);
    }

    // RetryCommand Property
    public static readonly DependencyProperty RetryCommandProperty =
        DependencyProperty.Register(
            nameof(RetryCommand),
            typeof(ICommand),
            typeof(ErrorStateView),
            new PropertyMetadata(null));

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    // ShowRetryButton Property
    public static readonly DependencyProperty ShowRetryButtonProperty =
        DependencyProperty.Register(
            nameof(ShowRetryButton),
            typeof(bool),
            typeof(ErrorStateView),
            new PropertyMetadata(true));

    public bool ShowRetryButton
    {
        get => (bool)GetValue(ShowRetryButtonProperty);
        set => SetValue(ShowRetryButtonProperty, value);
    }
}
