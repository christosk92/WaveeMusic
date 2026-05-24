using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Playback;

/// <summary>
/// Player-bar mode button whose active state tints the glyph foreground without
/// using a checked/accent background fill.
/// </summary>
public sealed class TransportModeButton : Button
{
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(TransportModeButton),
            new PropertyMetadata(false, (d, _) => ((TransportModeButton)d).UpdateActiveState(true)));

    public Brush? ActiveForeground
    {
        get => (Brush?)GetValue(ActiveForegroundProperty);
        set => SetValue(ActiveForegroundProperty, value);
    }

    public static readonly DependencyProperty ActiveForegroundProperty =
        DependencyProperty.Register(
            nameof(ActiveForeground), typeof(Brush), typeof(TransportModeButton),
            new PropertyMetadata(null, (d, _) => ((TransportModeButton)d).ApplyCurrentForeground()));

    public Brush? InactiveForeground
    {
        get => (Brush?)GetValue(InactiveForegroundProperty);
        set => SetValue(InactiveForegroundProperty, value);
    }

    public static readonly DependencyProperty InactiveForegroundProperty =
        DependencyProperty.Register(
            nameof(InactiveForeground), typeof(Brush), typeof(TransportModeButton),
            new PropertyMetadata(null, (d, _) => ((TransportModeButton)d).ApplyCurrentForeground()));

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateActiveState(false);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ApplyCurrentForeground();
    }

    private void UpdateActiveState(bool useTransitions)
    {
        VisualStateManager.GoToState(this, IsActive ? "Active" : "Inactive", useTransitions);
        ApplyCurrentForeground();
    }

    private void ApplyCurrentForeground()
    {
        var brush = IsActive
            ? ActiveForeground ?? Foreground
            : InactiveForeground ?? Foreground;

        if (brush is null)
            return;

        Foreground = brush;
        ApplyForeground(Content, brush);
    }

    private static void ApplyForeground(object? content, Brush brush)
    {
        switch (content)
        {
            case FontIcon icon:
                icon.Foreground = brush;
                break;
            case IconElement icon:
                icon.Foreground = brush;
                break;
            case TextBlock text:
                text.Foreground = brush;
                break;
            case Control control:
                control.Foreground = brush;
                break;
        }
    }
}
