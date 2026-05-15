using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wavee.UI.WinUI.Styles;
using AppNotificationSeverity = Wavee.UI.WinUI.Data.Models.NotificationSeverity;

namespace Wavee.UI.WinUI.Controls.Notifications;

/// <summary>
/// Floating Photos-style toast chip used by the shell. Centered, rounded,
/// auto-dismissing, with optional action button and explicit close. The
/// shell binds this to <see cref="Data.Contracts.INotificationService"/> via
/// <see cref="ViewModels.ShellViewModel"/>.
/// </summary>
public sealed partial class NotificationToast : UserControl
{
    public NotificationToast()
    {
        InitializeComponent();
        ApplySeverity(Severity);
    }

    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(NotificationToast),
            new PropertyMetadata(false, OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(NotificationToast),
            new PropertyMetadata(null, OnMessageChanged));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public static readonly DependencyProperty SeverityProperty =
        DependencyProperty.Register(nameof(Severity), typeof(AppNotificationSeverity), typeof(NotificationToast),
            new PropertyMetadata(AppNotificationSeverity.Informational, OnSeverityChanged));

    public AppNotificationSeverity Severity
    {
        get => (AppNotificationSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public static readonly DependencyProperty ActionLabelProperty =
        DependencyProperty.Register(nameof(ActionLabel), typeof(string), typeof(NotificationToast),
            new PropertyMetadata(null, OnActionLabelChanged));

    public string? ActionLabel
    {
        get => (string?)GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public static readonly DependencyProperty IsActionEnabledProperty =
        DependencyProperty.Register(nameof(IsActionEnabled), typeof(bool), typeof(NotificationToast),
            new PropertyMetadata(true, OnIsActionEnabledChanged));

    public bool IsActionEnabled
    {
        get => (bool)GetValue(IsActionEnabledProperty);
        set => SetValue(IsActionEnabledProperty, value);
    }

    public event RoutedEventHandler? ActionClick;
    public event RoutedEventHandler? CloseRequested;

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toast = (NotificationToast)d;
        toast.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toast = (NotificationToast)d;
        var text = e.NewValue as string ?? string.Empty;
        toast.MessageText.Text = text;
        ToolTipService.SetToolTip(toast.MessageText, text);
    }

    private static void OnSeverityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NotificationToast)d).ApplySeverity((AppNotificationSeverity)e.NewValue);
    }

    private static void OnActionLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toast = (NotificationToast)d;
        var label = e.NewValue as string;
        toast.ActionButton.Content = label;
        toast.ActionButton.Visibility = string.IsNullOrEmpty(label)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static void OnIsActionEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((NotificationToast)d).ActionButton.IsEnabled = (bool)e.NewValue;
    }

    private void ApplySeverity(AppNotificationSeverity severity)
    {
        var (glyph, brushKey) = severity switch
        {
            AppNotificationSeverity.Success => (FluentGlyphs.CheckMark, "SystemFillColorSuccessBrush"),
            AppNotificationSeverity.Warning => (FluentGlyphs.Warning, "SystemFillColorCautionBrush"),
            AppNotificationSeverity.Error => (FluentGlyphs.ErrorBadge, "SystemFillColorCriticalBrush"),
            _ => (FluentGlyphs.Info, "AccentFillColorDefaultBrush"),
        };

        SeverityIcon.Glyph = glyph;
        if (Application.Current.Resources.TryGetValue(brushKey, out var brush) && brush is Brush themed)
            SeverityIcon.Foreground = themed;
    }

    private void OnActionClick(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, e);
}
