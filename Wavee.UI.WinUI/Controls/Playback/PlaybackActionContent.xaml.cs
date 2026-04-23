using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Playback;

public sealed partial class PlaybackActionContent : UserControl
{
    public static readonly DependencyProperty IdleGlyphProperty =
        DependencyProperty.Register(nameof(IdleGlyph), typeof(string), typeof(PlaybackActionContent),
            new PropertyMetadata("\uE768", OnVisualPropertyChanged));

    public static readonly DependencyProperty ActiveGlyphProperty =
        DependencyProperty.Register(nameof(ActiveGlyph), typeof(string), typeof(PlaybackActionContent),
            new PropertyMetadata("\uE769", OnVisualPropertyChanged));

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(PlaybackActionContent),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty IsPendingProperty =
        DependencyProperty.Register(nameof(IsPending), typeof(bool), typeof(PlaybackActionContent),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty ShowLabelProperty =
        DependencyProperty.Register(nameof(ShowLabel), typeof(bool), typeof(PlaybackActionContent),
            new PropertyMetadata(false, OnVisualPropertyChanged));

    public static readonly DependencyProperty PlayTextProperty =
        DependencyProperty.Register(nameof(PlayText), typeof(string), typeof(PlaybackActionContent),
            new PropertyMetadata("Play", OnVisualPropertyChanged));

    public static readonly DependencyProperty PauseTextProperty =
        DependencyProperty.Register(nameof(PauseText), typeof(string), typeof(PlaybackActionContent),
            new PropertyMetadata("Pause", OnVisualPropertyChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(PlaybackActionContent),
            new PropertyMetadata(16.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty SpinnerSizeProperty =
        DependencyProperty.Register(nameof(SpinnerSize), typeof(double), typeof(PlaybackActionContent),
            new PropertyMetadata(18.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty LabelFontSizeProperty =
        DependencyProperty.Register(nameof(LabelFontSize), typeof(double), typeof(PlaybackActionContent),
            new PropertyMetadata(12.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(PlaybackActionContent),
            new PropertyMetadata(8.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty ActionForegroundProperty =
        DependencyProperty.Register(nameof(ActionForeground), typeof(Brush), typeof(PlaybackActionContent),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public string IdleGlyph
    {
        get => (string)GetValue(IdleGlyphProperty);
        set => SetValue(IdleGlyphProperty, value);
    }

    public string ActiveGlyph
    {
        get => (string)GetValue(ActiveGlyphProperty);
        set => SetValue(ActiveGlyphProperty, value);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsPending
    {
        get => (bool)GetValue(IsPendingProperty);
        set => SetValue(IsPendingProperty, value);
    }

    public bool ShowLabel
    {
        get => (bool)GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    public string PlayText
    {
        get => (string)GetValue(PlayTextProperty);
        set => SetValue(PlayTextProperty, value);
    }

    public string PauseText
    {
        get => (string)GetValue(PauseTextProperty);
        set => SetValue(PauseTextProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double SpinnerSize
    {
        get => (double)GetValue(SpinnerSizeProperty);
        set => SetValue(SpinnerSizeProperty, value);
    }

    public double LabelFontSize
    {
        get => (double)GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public Brush? ActionForeground
    {
        get => (Brush?)GetValue(ActionForegroundProperty);
        set => SetValue(ActionForegroundProperty, value);
    }

    public PlaybackActionContent()
    {
        InitializeComponent();
        UpdateVisualState();
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PlaybackActionContent)d).UpdateVisualState();

    private void UpdateVisualState()
    {
        if (LayoutRoot == null)
            return;

        LayoutRoot.Spacing = Spacing;

        double hostSize = Math.Max(IconSize, SpinnerSize);
        IconHost.Width = hostSize;
        IconHost.Height = hostSize;

        ActionGlyph.Glyph = IsPlaying ? ActiveGlyph : IdleGlyph;
        ActionGlyph.FontSize = IconSize;
        ActionGlyph.Visibility = IsPending ? Visibility.Collapsed : Visibility.Visible;

        ActionSpinner.Width = SpinnerSize;
        ActionSpinner.Height = SpinnerSize;
        ActionSpinner.IsActive = IsPending;
        ActionSpinner.Visibility = IsPending ? Visibility.Visible : Visibility.Collapsed;

        ActionLabel.Text = IsPlaying ? PauseText : PlayText;
        ActionLabel.FontSize = LabelFontSize;
        ActionLabel.Visibility = ShowLabel ? Visibility.Visible : Visibility.Collapsed;

        ApplyForeground(ActionForeground);
    }

    private void ApplyForeground(Brush? foreground)
    {
        if (foreground == null)
        {
            ActionGlyph.ClearValue(FontIcon.ForegroundProperty);
            ActionSpinner.ClearValue(ProgressRing.ForegroundProperty);
            ActionLabel.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        ActionGlyph.Foreground = foreground;
        ActionSpinner.Foreground = foreground;
        ActionLabel.Foreground = foreground;
    }
}
