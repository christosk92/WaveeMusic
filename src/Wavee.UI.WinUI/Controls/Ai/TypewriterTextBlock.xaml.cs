using System;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Wavee.UI.WinUI.Controls.Ai;

public sealed partial class TypewriterTextBlock : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(string.Empty, OnRevealInputChanged));

    public static readonly DependencyProperty IsRevealAnimatedProperty = DependencyProperty.Register(
        nameof(IsRevealAnimated),
        typeof(bool),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(true, OnRevealInputChanged));

    public static readonly DependencyProperty WordIntervalMsProperty = DependencyProperty.Register(
        nameof(WordIntervalMs),
        typeof(double),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(80d, OnRevealInputChanged));

    public static readonly DependencyProperty RaiseRevealCompletedProperty = DependencyProperty.Register(
        nameof(RaiseRevealCompleted),
        typeof(bool),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(true));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight),
        typeof(double),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(20d));

    public static readonly DependencyProperty MaxLinesProperty = DependencyProperty.Register(
        nameof(MaxLines),
        typeof(int),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(4));

    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(
        nameof(TextWrapping),
        typeof(TextWrapping),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(TextWrapping.Wrap));

    public static readonly DependencyProperty TextTrimmingProperty = DependencyProperty.Register(
        nameof(TextTrimming),
        typeof(TextTrimming),
        typeof(TypewriterTextBlock),
        new PropertyMetadata(TextTrimming.CharacterEllipsis));

    private readonly StringBuilder _revealedText = new();
    private DispatcherTimer? _revealTimer;
    private string[] _tokens = Array.Empty<string>();
    private int _tokenIndex;

    public TypewriterTextBlock()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public event EventHandler<RevealCompletedEventArgs>? RevealCompleted;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsRevealAnimated
    {
        get => (bool)GetValue(IsRevealAnimatedProperty);
        set => SetValue(IsRevealAnimatedProperty, value);
    }

    public double WordIntervalMs
    {
        get => (double)GetValue(WordIntervalMsProperty);
        set => SetValue(WordIntervalMsProperty, value);
    }

    public bool RaiseRevealCompleted
    {
        get => (bool)GetValue(RaiseRevealCompletedProperty);
        set => SetValue(RaiseRevealCompletedProperty, value);
    }

    public double LineHeight
    {
        get => (double)GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public TextWrapping TextWrapping
    {
        get => (TextWrapping)GetValue(TextWrappingProperty);
        set => SetValue(TextWrappingProperty, value);
    }

    public TextTrimming TextTrimming
    {
        get => (TextTrimming)GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    public InlineCollection BodyInlines => PART_TextBlock.Inlines;

    private static void OnRevealInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TypewriterTextBlock control)
            control.RestartReveal();
    }

    private void RestartReveal()
    {
        StopTimer();

        var text = Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            BodyInlines.Clear();
            _tokens = Array.Empty<string>();
            _tokenIndex = 0;
            _revealedText.Clear();
            return;
        }

        if (!IsRevealAnimated)
        {
            SetPlainText(text);
            OnRevealCompleted(text);
            return;
        }

        _tokens = BuildWordTokens(text);
        if (_tokens.Length == 0)
        {
            SetPlainText(text);
            OnRevealCompleted(text);
            return;
        }

        _tokenIndex = 0;
        _revealedText.Clear();
        BodyInlines.Clear();

        _revealTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(1d, WordIntervalMs)),
        };
        _revealTimer.Tick += OnRevealTick;
        _revealTimer.Start();
    }

    private void OnRevealTick(object? sender, object e)
    {
        if (_tokenIndex >= _tokens.Length)
        {
            CompleteReveal();
            return;
        }

        _revealedText.Append(_tokens[_tokenIndex]);
        _tokenIndex++;
        SetPlainText(_revealedText.ToString());

        if (_tokenIndex >= _tokens.Length)
            CompleteReveal();
    }

    private void CompleteReveal()
    {
        StopTimer();
        var text = Text ?? string.Empty;
        SetPlainText(text);
        OnRevealCompleted(text);
    }

    private void OnRevealCompleted(string text)
    {
        if (RaiseRevealCompleted)
            RevealCompleted?.Invoke(this, new RevealCompletedEventArgs(text));
    }

    private void SetPlainText(string text)
    {
        BodyInlines.Clear();
        BodyInlines.Add(new Run { Text = text });
    }

    private void StopTimer()
    {
        if (_revealTimer is null)
            return;

        _revealTimer.Stop();
        _revealTimer.Tick -= OnRevealTick;
        _revealTimer = null;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopTimer();
    }

    private static string[] BuildWordTokens(string text)
    {
        var matches = Regex.Matches(text, @"\S+\s*");
        var tokens = new string[matches.Count];
        for (var i = 0; i < matches.Count; i++)
            tokens[i] = matches[i].Value;
        return tokens;
    }
}
