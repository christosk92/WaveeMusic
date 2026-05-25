using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Wavee.UI.WinUI.Controls.Ai;

public sealed partial class AiTextCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(AiTextCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(
        nameof(IsAvailable),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(false, OnVisualStateInputChanged));

    public static readonly DependencyProperty IsGeneratingProperty = DependencyProperty.Register(
        nameof(IsGenerating),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(false, OnVisualStateInputChanged));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(AiTextCard),
        new PropertyMetadata(string.Empty, OnTextInputChanged));

    public static readonly DependencyProperty IsTextFromCacheProperty = DependencyProperty.Register(
        nameof(IsTextFromCache),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(false, OnTextInputChanged));

    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(
        nameof(IsStreaming),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(false, OnStreamingChanged));

    public static readonly DependencyProperty WordIntervalMsProperty = DependencyProperty.Register(
        nameof(WordIntervalMs),
        typeof(double),
        typeof(AiTextCard),
        new PropertyMetadata(80d, OnTextInputChanged));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(AiTextCard),
        new PropertyMetadata(string.Empty, OnVisualStateInputChanged));

    public static readonly DependencyProperty PlaceholderCaptionProperty = DependencyProperty.Register(
        nameof(PlaceholderCaption),
        typeof(string),
        typeof(AiTextCard),
        new PropertyMetadata("thinking..."));

    public static readonly DependencyProperty FooterContentProperty = DependencyProperty.Register(
        nameof(FooterContent),
        typeof(object),
        typeof(AiTextCard),
        new PropertyMetadata(null, OnFooterContentChanged));

    public static readonly DependencyProperty ShowSparkleIconProperty = DependencyProperty.Register(
        nameof(ShowSparkleIcon),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(true, OnVisualStateInputChanged));

    public static readonly DependencyProperty ShowChromeProperty = DependencyProperty.Register(
        nameof(ShowChrome),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(true, OnChromeInputChanged));

    public static readonly DependencyProperty ShowHeaderProperty = DependencyProperty.Register(
        nameof(ShowHeader),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(true, OnVisualStateInputChanged));

    public static readonly DependencyProperty BodyMinHeightProperty = DependencyProperty.Register(
        nameof(BodyMinHeight),
        typeof(double),
        typeof(AiTextCard),
        new PropertyMetadata(96d, OnLayoutInputChanged));

    public static readonly DependencyProperty BodyMaxLinesProperty = DependencyProperty.Register(
        nameof(BodyMaxLines),
        typeof(int),
        typeof(AiTextCard),
        new PropertyMetadata(4, OnLayoutInputChanged));

    public static readonly DependencyProperty BodyTextTrimmingProperty = DependencyProperty.Register(
        nameof(BodyTextTrimming),
        typeof(TextTrimming),
        typeof(AiTextCard),
        new PropertyMetadata(TextTrimming.CharacterEllipsis, OnLayoutInputChanged));

    private bool _sawStreamingText;
    private readonly Brush? _defaultCardBackground;
    private readonly Brush? _defaultCardBorderBrush;

    public AiTextCard()
    {
        InitializeComponent();
        _defaultCardBackground = CardBorder.Background;
        _defaultCardBorderBrush = CardBorder.BorderBrush;
        BodyTextBlock.RevealCompleted += OnTypewriterRevealCompleted;
        UpdateFooterContent();
        ApplyChrome();
        ApplyLayoutInputs();
        ApplyTextInputs();
        UpdateVisualState();
    }

    public event EventHandler<RevealCompletedEventArgs>? RevealCompleted;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsAvailable
    {
        get => (bool)GetValue(IsAvailableProperty);
        set => SetValue(IsAvailableProperty, value);
    }

    public bool IsGenerating
    {
        get => (bool)GetValue(IsGeneratingProperty);
        set => SetValue(IsGeneratingProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsTextFromCache
    {
        get => (bool)GetValue(IsTextFromCacheProperty);
        set => SetValue(IsTextFromCacheProperty, value);
    }

    public bool IsStreaming
    {
        get => (bool)GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    public double WordIntervalMs
    {
        get => (double)GetValue(WordIntervalMsProperty);
        set => SetValue(WordIntervalMsProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    public string PlaceholderCaption
    {
        get => (string)GetValue(PlaceholderCaptionProperty);
        set => SetValue(PlaceholderCaptionProperty, value);
    }

    public object? FooterContent
    {
        get => GetValue(FooterContentProperty);
        set => SetValue(FooterContentProperty, value);
    }

    public bool ShowSparkleIcon
    {
        get => (bool)GetValue(ShowSparkleIconProperty);
        set => SetValue(ShowSparkleIconProperty, value);
    }

    public bool ShowChrome
    {
        get => (bool)GetValue(ShowChromeProperty);
        set => SetValue(ShowChromeProperty, value);
    }

    public bool ShowHeader
    {
        get => (bool)GetValue(ShowHeaderProperty);
        set => SetValue(ShowHeaderProperty, value);
    }

    public double BodyMinHeight
    {
        get => (double)GetValue(BodyMinHeightProperty);
        set => SetValue(BodyMinHeightProperty, value);
    }

    public int BodyMaxLines
    {
        get => (int)GetValue(BodyMaxLinesProperty);
        set => SetValue(BodyMaxLinesProperty, value);
    }

    public TextTrimming BodyTextTrimming
    {
        get => (TextTrimming)GetValue(BodyTextTrimmingProperty);
        set => SetValue(BodyTextTrimmingProperty, value);
    }

    public InlineCollection BodyInlines => BodyTextBlock.BodyInlines;

    private static void OnVisualStateInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
            card.UpdateVisualState();
    }

    private static void OnTextInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
        {
            card.ApplyTextInputs();
            card.UpdateVisualState();
        }
    }

    private static void OnStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not AiTextCard card)
            return;

        var wasStreaming = e.OldValue is true;
        card.ApplyTextInputs();
        card.UpdateVisualState();

        if (wasStreaming
            && !card.IsStreaming
            && card._sawStreamingText
            && !string.IsNullOrWhiteSpace(card.Text))
        {
            card.RevealCompleted?.Invoke(card, new RevealCompletedEventArgs(card.Text));
        }
    }

    private static void OnFooterContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
            card.UpdateFooterContent();
    }

    private static void OnChromeInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
            card.ApplyChrome();
    }

    private static void OnLayoutInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
            card.ApplyLayoutInputs();
    }

    private void ApplyTextInputs()
    {
        var hasText = !string.IsNullOrWhiteSpace(Text);
        if (!hasText)
            _sawStreamingText = false;
        else if (IsStreaming)
            _sawStreamingText = true;

        BodyTextBlock.WordIntervalMs = WordIntervalMs;
        BodyTextBlock.RaiseRevealCompleted = !IsStreaming;
        BodyTextBlock.IsRevealAnimated = !IsStreaming && !IsTextFromCache && !_sawStreamingText;
        BodyTextBlock.Text = Text ?? string.Empty;
    }

    private void ApplyChrome()
    {
        CardBorder.Background = ShowChrome ? _defaultCardBackground : null;
        CardBorder.BorderBrush = ShowChrome ? _defaultCardBorderBrush : null;
        CardBorder.BorderThickness = ShowChrome ? new Thickness(1) : new Thickness(0);
        CardBorder.CornerRadius = ShowChrome ? new CornerRadius(14) : new CornerRadius(0);
        CardBorder.Padding = ShowChrome ? new Thickness(22, 18, 22, 18) : new Thickness(0);
    }

    private void ApplyLayoutInputs()
    {
        BodyHost.MinHeight = Math.Max(0, BodyMinHeight);
        BodyTextBlock.MaxLines = Math.Max(0, BodyMaxLines);
        BodyTextBlock.TextTrimming = BodyTextTrimming;
    }

    private void UpdateVisualState()
    {
        Visibility = IsAvailable ? Visibility.Visible : Visibility.Collapsed;

        var hasText = !string.IsNullOrWhiteSpace(Text);
        var showPlaceholder = IsAvailable && IsGenerating && !hasText;
        var showEmpty = IsAvailable && !showPlaceholder && !hasText && !string.IsNullOrWhiteSpace(EmptyText);

        ThinkingPlaceholder.Visibility = showPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        BodyTextBlock.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        EmptyTextBlock.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
        BodyHost.Visibility = showPlaceholder || hasText || showEmpty ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = ShowHeader ? Visibility.Visible : Visibility.Collapsed;
        FooterPresenter.Visibility = FooterContent is null ? Visibility.Collapsed : Visibility.Visible;
        HeaderSparkle.Visibility = ShowSparkleIcon ? Visibility.Visible : Visibility.Collapsed;
        HeaderSparkle.State = showPlaceholder || IsStreaming ? "Generating" : "Normal";
    }

    private void UpdateFooterContent()
    {
        FooterPresenter.Content = FooterContent;
        FooterPresenter.Visibility = FooterContent is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTypewriterRevealCompleted(object? sender, RevealCompletedEventArgs e)
    {
        RevealCompleted?.Invoke(this, e);
    }
}
