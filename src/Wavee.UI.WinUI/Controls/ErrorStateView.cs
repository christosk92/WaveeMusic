using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// A templatable control that displays an error state with icon, title, message, and retry button.
/// Override the default template via <see cref="Control.Template"/> for full customization.
/// </summary>
public sealed class ErrorStateView : Control
{
    public ErrorStateView()
    {
        DefaultStyleKey = typeof(ErrorStateView);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>
    /// The icon glyph to display (Segoe MDL2 Assets). Defaults to warning icon.
    /// </summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ErrorStateView),
            new PropertyMetadata("\uE783"));

    /// <summary>
    /// The title text. Defaults to "Something went wrong".
    /// </summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ErrorStateView),
            new PropertyMetadata("Something went wrong"));

    /// <summary>
    /// The error message text (typically Exception.Message).
    /// </summary>
    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(ErrorStateView),
            new PropertyMetadata(null));

    /// <summary>
    /// Command executed when the retry button is clicked. If null, the retry button is hidden.
    /// </summary>
    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public static readonly DependencyProperty RetryCommandProperty =
        DependencyProperty.Register(nameof(RetryCommand), typeof(ICommand), typeof(ErrorStateView),
            new PropertyMetadata(null));

    /// <summary>
    /// The retry button text. Defaults to "Retry".
    /// </summary>
    public string RetryButtonText
    {
        get => (string)GetValue(RetryButtonTextProperty);
        set => SetValue(RetryButtonTextProperty, value);
    }

    public static readonly DependencyProperty RetryButtonTextProperty =
        DependencyProperty.Register(nameof(RetryButtonText), typeof(string), typeof(ErrorStateView),
            new PropertyMetadata("Retry"));
}
