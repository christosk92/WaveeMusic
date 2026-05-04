using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Wavee.UI.WinUI.Controls.Comments;

/// <summary>
/// Comment composer — TextBox + character counter + submit button + optional
/// status row. Used by <see cref="CommentsList"/> and (transitively) by both
/// the library episode detail and the right-sidebar comments layout. Raises
/// <see cref="SubmitRequested"/> on click; the host wires consent + command
/// invocation. <see cref="SubmitCommand"/> can also be bound directly when no
/// host-side preflight is needed.
/// </summary>
public sealed partial class CommentComposer : UserControl
{
    public static readonly DependencyProperty DraftProperty =
        DependencyProperty.Register(
            nameof(Draft), typeof(string), typeof(CommentComposer),
            new PropertyMetadata("", OnDraftChanged));

    public static readonly DependencyProperty CharacterCountProperty =
        DependencyProperty.Register(
            nameof(CharacterCount), typeof(string), typeof(CommentComposer),
            new PropertyMetadata("", OnCharacterCountChanged));

    public static readonly DependencyProperty StatusTextProperty =
        DependencyProperty.Register(
            nameof(StatusText), typeof(string), typeof(CommentComposer),
            new PropertyMetadata(null, OnStatusTextChanged));

    public static readonly DependencyProperty SubmitCommandProperty =
        DependencyProperty.Register(
            nameof(SubmitCommand), typeof(ICommand), typeof(CommentComposer),
            new PropertyMetadata(null));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText), typeof(string), typeof(CommentComposer),
            new PropertyMetadata("Add a public comment...", OnPlaceholderChanged));

    public static readonly DependencyProperty SubmitButtonTextProperty =
        DependencyProperty.Register(
            nameof(SubmitButtonText), typeof(string), typeof(CommentComposer),
            new PropertyMetadata("Comment", OnSubmitButtonTextChanged));

    public static readonly DependencyProperty MaxLengthProperty =
        DependencyProperty.Register(
            nameof(MaxLength), typeof(int), typeof(CommentComposer),
            new PropertyMetadata(500, OnMaxLengthChanged));

    public static readonly DependencyProperty MinTextBoxHeightProperty =
        DependencyProperty.Register(
            nameof(MinTextBoxHeight), typeof(double), typeof(CommentComposer),
            new PropertyMetadata(64.0, OnMinTextBoxHeightChanged));

    public static readonly DependencyProperty ShowStatusAsInfoBarProperty =
        DependencyProperty.Register(
            nameof(ShowStatusAsInfoBar), typeof(bool), typeof(CommentComposer),
            new PropertyMetadata(true, OnStatusStyleChanged));

    public static readonly DependencyProperty IsSubmitEnabledProperty =
        DependencyProperty.Register(
            nameof(IsSubmitEnabled), typeof(bool), typeof(CommentComposer),
            new PropertyMetadata(true, OnIsSubmitEnabledChanged));

    public static readonly DependencyProperty SubmitButtonStyleProperty =
        DependencyProperty.Register(
            nameof(SubmitButtonStyle), typeof(Style), typeof(CommentComposer),
            new PropertyMetadata(null, OnSubmitButtonStyleChanged));

    public string Draft
    {
        get => (string)GetValue(DraftProperty);
        set => SetValue(DraftProperty, value);
    }

    public string CharacterCount
    {
        get => (string)GetValue(CharacterCountProperty);
        set => SetValue(CharacterCountProperty, value);
    }

    public string? StatusText
    {
        get => (string?)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    public ICommand? SubmitCommand
    {
        get => (ICommand?)GetValue(SubmitCommandProperty);
        set => SetValue(SubmitCommandProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public string SubmitButtonText
    {
        get => (string)GetValue(SubmitButtonTextProperty);
        set => SetValue(SubmitButtonTextProperty, value);
    }

    public int MaxLength
    {
        get => (int)GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public double MinTextBoxHeight
    {
        get => (double)GetValue(MinTextBoxHeightProperty);
        set => SetValue(MinTextBoxHeightProperty, value);
    }

    public bool ShowStatusAsInfoBar
    {
        get => (bool)GetValue(ShowStatusAsInfoBarProperty);
        set => SetValue(ShowStatusAsInfoBarProperty, value);
    }

    public bool IsSubmitEnabled
    {
        get => (bool)GetValue(IsSubmitEnabledProperty);
        set => SetValue(IsSubmitEnabledProperty, value);
    }

    public Style? SubmitButtonStyle
    {
        get => (Style?)GetValue(SubmitButtonStyleProperty);
        set => SetValue(SubmitButtonStyleProperty, value);
    }

    /// <summary>Fired when the user clicks the Submit button. Host handles
    /// consent gates + command invocation. <see cref="SubmitCommand"/> is
    /// invoked automatically if no host has subscribed.</summary>
    public event TypedEventHandler<CommentComposer, RoutedEventArgs>? SubmitRequested;

    public CommentComposer()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyAll();
        };
    }

    private void ApplyAll()
    {
        if (DraftBox.Text != (Draft ?? ""))
            DraftBox.Text = Draft ?? "";
        DraftBox.PlaceholderText = PlaceholderText ?? "";
        DraftBox.MaxLength = MaxLength;
        DraftBox.MinHeight = MinTextBoxHeight;

        CharCountText.Text = CharacterCount ?? "";
        SubmitButton.Content = SubmitButtonText ?? "";
        SubmitButton.IsEnabled = IsSubmitEnabled;
        if (SubmitButtonStyle is not null)
            SubmitButton.Style = SubmitButtonStyle;

        ApplyStatus();
    }

    private void ApplyStatus()
    {
        var text = StatusText;
        var hasStatus = !string.IsNullOrEmpty(text);
        if (ShowStatusAsInfoBar)
        {
            StatusInfoBar.Message = text ?? "";
            StatusInfoBar.IsOpen = hasStatus;
            StatusInfoBar.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
            InlineStatusText.Visibility = Visibility.Collapsed;
        }
        else
        {
            InlineStatusText.Text = text ?? "";
            InlineStatusText.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
            StatusInfoBar.Visibility = Visibility.Collapsed;
        }
    }

    private static void OnDraftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c && c.DraftBox.Text != (e.NewValue as string ?? ""))
            c.DraftBox.Text = e.NewValue as string ?? "";
    }

    private static void OnCharacterCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.CharCountText.Text = e.NewValue as string ?? "";
    }

    private static void OnStatusTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.ApplyStatus();
    }

    private static void OnStatusStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.ApplyStatus();
    }

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.DraftBox.PlaceholderText = e.NewValue as string ?? "";
    }

    private static void OnSubmitButtonTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.SubmitButton.Content = e.NewValue as string ?? "";
    }

    private static void OnMaxLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.DraftBox.MaxLength = (int)e.NewValue;
    }

    private static void OnMinTextBoxHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.DraftBox.MinHeight = (double)e.NewValue;
    }

    private static void OnIsSubmitEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c) c.SubmitButton.IsEnabled = (bool)e.NewValue;
    }

    private static void OnSubmitButtonStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CommentComposer c && e.NewValue is Style s) c.SubmitButton.Style = s;
    }

    private void DraftBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Draft != DraftBox.Text)
            Draft = DraftBox.Text;
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        // Host gets first crack at the submit (consent dialog, command preflight).
        // If nobody subscribes, fall through to the bound command directly.
        if (SubmitRequested is { } handler)
        {
            handler.Invoke(this, e);
            return;
        }
        if (SubmitCommand is { } cmd && cmd.CanExecute(null))
            cmd.Execute(null);
    }
}
