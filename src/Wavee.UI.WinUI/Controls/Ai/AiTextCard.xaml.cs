using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

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

    public static readonly DependencyProperty IsCollapsibleProperty = DependencyProperty.Register(
        nameof(IsCollapsible),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(true, OnCollapseInputChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded),
        typeof(bool),
        typeof(AiTextCard),
        new PropertyMetadata(false, OnExpandedChanged));

    public static readonly DependencyProperty CollapsedMaxLinesProperty = DependencyProperty.Register(
        nameof(CollapsedMaxLines),
        typeof(int),
        typeof(AiTextCard),
        new PropertyMetadata(2, OnCollapseInputChanged));

    private bool _sawStreamingText;
    // Set during the animated transition so the layout pass triggered by the
    // text reveal doesn't yank MaxHeight away from the storyboard mid-flight.
    private bool _suppressClipUpdate;
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

    /// <summary>When true (default), the card opens collapsed to
    /// <see cref="CollapsedMaxLines"/> and toggles full content on tap. Set to
    /// false to force the card always-expanded (e.g. inside an existing
    /// disclosure surface that already controls visibility).</summary>
    public bool IsCollapsible
    {
        get => (bool)GetValue(IsCollapsibleProperty);
        set => SetValue(IsCollapsibleProperty, value);
    }

    /// <summary>Two-way: current open/closed state of the body. Tapping the
    /// card flips this when <see cref="IsCollapsible"/> is true.</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Number of lines visible in the collapsed state.</summary>
    public int CollapsedMaxLines
    {
        get => (int)GetValue(CollapsedMaxLinesProperty);
        set => SetValue(CollapsedMaxLinesProperty, value);
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
        // MinHeight is reconciled inside UpdateVisualState — it has to know
        // whether we're showing text vs the thinking placeholder.
        BodyTextBlock.MaxLines = Math.Max(0, BodyMaxLines);
        BodyTextBlock.TextTrimming = BodyTextTrimming;
        UpdateCollapseState(animated: false);
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

        // When collapsed-with-text, drop the placeholder min-height so the body
        // sizes to the visible 2-line clip; otherwise honour BodyMinHeight so
        // the thinking-placeholder / empty-state have breathing room.
        BodyHost.MinHeight = IsCollapsible && !IsExpanded && hasText
            ? 0
            : Math.Max(0, BodyMinHeight);

        // Hide the chevron when there's no content yet — there's nothing to
        // expand to. It re-renders after the body lands.
        ExpandChevron.Visibility = IsCollapsible && hasText
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateFooterContent()
    {
        FooterPresenter.Content = FooterContent;
        FooterPresenter.Visibility = FooterContent is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTypewriterRevealCompleted(object? sender, RevealCompletedEventArgs e)
    {
        RevealCompleted?.Invoke(this, e);
        // Re-evaluate clip height — the final inlines may differ from the
        // streamed approximation (linkifier replaces plain runs with hyperlinks
        // which can shift line wrap).
        UpdateCollapseState(animated: false);
    }

    private void OnBodyClipHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        BodyClipHost.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(
                0,
                0,
                Math.Max(0, e.NewSize.Width),
                Math.Max(0, e.NewSize.Height)),
        };
    }

    // ── Collapse / expand wiring ───────────────────────────────────────────

    private static void OnCollapseInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
            card.UpdateCollapseState(animated: false);
    }

    private static void OnExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AiTextCard card)
            card.UpdateCollapseState(animated: true);
    }

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!IsCollapsible || !IsAvailable)
            return;

        // Ignore taps that originated on an interactive child (hyperlinks in
        // the body, footer buttons). Hyperlinks set OriginalSource to a Run
        // inside the Hyperlink, so a direct ancestor walk would mis-classify
        // them — instead check whether the source is the Run inside our
        // BodyTextBlock and whether the parent inline is a Hyperlink.
        if (e.OriginalSource is FrameworkElement source)
        {
            var walker = source;
            while (walker is not null)
            {
                if (walker == CardBorder) break;
                if (walker is ButtonBase or HyperlinkButton)
                    return;
                walker = walker.Parent as FrameworkElement;
            }
        }

        IsExpanded = !IsExpanded;
        e.Handled = true;
    }

    private void UpdateCollapseState(bool animated)
    {
        if (_suppressClipUpdate)
            return;

        var clip = BodyClipHost;
        var chevron = ExpandChevronTransform;
        var fade = FadeOverlay;
        if (clip is null || fade is null || chevron is null)
            return;

        // Not collapsible — uncap the clip and hide both affordances.
        if (!IsCollapsible)
        {
            clip.MaxHeight = double.PositiveInfinity;
            fade.Opacity = 0;
            chevron.Angle = 0;
            ExpandChevron.Visibility = Visibility.Collapsed;
            return;
        }

        ExpandChevron.Visibility = Visibility.Visible;

        var collapsedHeight = Math.Max(1, CollapsedMaxLines) * Math.Max(12, BodyTextBlock.LineHeight) + 8;
        var targetMaxHeight = IsExpanded ? double.PositiveInfinity : collapsedHeight;
        var targetFade = 0.0;
        var targetAngle = IsExpanded ? 180.0 : 0.0;

        if (!animated)
        {
            clip.MaxHeight = targetMaxHeight;
            fade.Opacity = targetFade;
            chevron.Angle = targetAngle;
            return;
        }

        // Storyboards can't animate to or from PositiveInfinity, so we resolve
        // both endpoints to finite values measured from layout.
        var sb = new Storyboard();
        if (IsExpanded)
        {
            // Expanding: snap MaxHeight to the current rendered height so the
            // animation has a finite starting point, briefly uncap to measure,
            // then animate up to the measured value.
            var startHeight = double.IsFinite(clip.MaxHeight)
                ? clip.MaxHeight
                : Math.Max(collapsedHeight, clip.ActualHeight);
            clip.MaxHeight = double.PositiveInfinity;
            clip.UpdateLayout();
            var measured = Math.Max(collapsedHeight, clip.ActualHeight);
            clip.MaxHeight = startHeight;

            sb.Children.Add(MakeMaxHeightAnimation(clip, measured, durationMs: 260));
        }
        else
        {
            // Collapsing: if MaxHeight is currently unbounded (PositiveInfinity),
            // pin it to the rendered ActualHeight so the storyboard has a finite
            // starting point — otherwise the height would snap to the target
            // instantly, defeating the animation.
            if (!double.IsFinite(clip.MaxHeight))
                clip.MaxHeight = clip.ActualHeight;

            sb.Children.Add(MakeMaxHeightAnimation(clip, collapsedHeight, durationMs: 220));
        }

        fade.Opacity = 0;
        sb.Children.Add(MakeRotationAnimation(chevron, targetAngle, durationMs: 220));

        // After the expand animation lands, drop the clip so a later text
        // change (linkifier, streaming refinement) doesn't get capped at a
        // stale measured value.
        if (IsExpanded)
        {
            sb.Completed += (_, _) =>
            {
                if (IsExpanded && IsCollapsible)
                    clip.MaxHeight = double.PositiveInfinity;
            };
        }

        _suppressClipUpdate = true;
        try { sb.Begin(); }
        finally { _suppressClipUpdate = false; }
    }

    private static DoubleAnimation MakeMaxHeightAnimation(FrameworkElement target, double to, int durationMs)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "MaxHeight");
        return animation;
    }

    private static DoubleAnimation MakeOpacityAnimation(UIElement target, double to, int durationMs)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        return animation;
    }

    private static DoubleAnimation MakeRotationAnimation(RotateTransform target, double to, int durationMs)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Angle");
        return animation;
    }
}
