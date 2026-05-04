using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// A reusable heart/like button that toggles between filled and outline states.
/// Bind IsLiked to your view model's liked state, and Command to your toggle command.
/// </summary>
public sealed partial class HeartButton : UserControl
{
    private const string FilledHeartGlyph = "\uEB52";
    private const string OutlineHeartGlyph = "\uEB51";

    public static readonly DependencyProperty IsLikedProperty =
        DependencyProperty.Register(
            nameof(IsLiked),
            typeof(bool),
            typeof(HeartButton),
            new PropertyMetadata(false, OnIsLikedChanged));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(HeartButton),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty =
        DependencyProperty.Register(
            nameof(CommandParameter),
            typeof(object),
            typeof(HeartButton),
            new PropertyMetadata(null));

    public bool IsLiked
    {
        get => (bool)GetValue(IsLikedProperty);
        set => SetValue(IsLikedProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public HeartButton()
    {
        InitializeComponent();
        UpdateVisualState(false);
    }

    private static void OnIsLikedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HeartButton)d).UpdateVisualState((bool)e.NewValue);
    }

    private void UpdateVisualState(bool isLiked)
    {
        HeartIcon.Glyph = isLiked ? FilledHeartGlyph : OutlineHeartGlyph;
        ToolTipService.SetToolTip(InternalButton,
            isLiked ? "Remove from Liked Songs" : "Save to Liked Songs");
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        var param = CommandParameter;
        if (Command?.CanExecute(param) == true)
            Command.Execute(param);
    }
}
