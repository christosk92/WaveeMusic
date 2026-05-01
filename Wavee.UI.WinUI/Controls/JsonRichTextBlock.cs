using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Selectable JSON/text viewer with syntax highlighting, line numbers, wrapping, and large-payload guards.
/// </summary>
public sealed partial class JsonRichTextBlock : UserControl
{
    private const int MaxViewerChars = 500_000;
    private const int MaxPrettyPrintChars = 500_000;
    private const int MaxFormattedTokens = 40_000;
    private const int MaxLines = 20_000;

    private static readonly Color KeyColor = Color.FromArgb(255, 86, 156, 214);
    private static readonly Color StringColor = Color.FromArgb(255, 206, 145, 120);
    private static readonly Color NumberColor = Color.FromArgb(255, 181, 206, 168);
    private static readonly Color BoolColor = Color.FromArgb(255, 86, 156, 214);
    private static readonly Color NullColor = Color.FromArgb(255, 128, 128, 128);
    private static readonly Color PunctuationColor = Color.FromArgb(255, 150, 150, 150);
    private static readonly Color SearchBackgroundColor = Color.FromArgb(110, 255, 215, 0);
    private static readonly Color TextColor = Color.FromArgb(255, 220, 220, 220);

    private readonly RichEditBox _editor;
    private readonly ScrollViewer _lineNumberScrollViewer;
    private readonly TextBlock _lineNumbers;
    private readonly TextBlock _statusText;
    private readonly ToggleButton _wrapToggle;
    private ScrollViewer? _editorScrollViewer;
    private string _renderedText = string.Empty;
    private IReadOnlyList<SyntaxToken> _tokens = [];
    private int _renderVersion;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(JsonRichTextBlock),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty SearchTextProperty =
        DependencyProperty.Register(nameof(SearchText), typeof(string), typeof(JsonRichTextBlock),
            new PropertyMetadata(string.Empty, OnSearchTextChanged));

    public static readonly DependencyProperty WordWrapProperty =
        DependencyProperty.Register(nameof(WordWrap), typeof(bool), typeof(JsonRichTextBlock),
            new PropertyMetadata(true, OnWordWrapChanged));

    public static readonly DependencyProperty ShowLineNumbersProperty =
        DependencyProperty.Register(nameof(ShowLineNumbers), typeof(bool), typeof(JsonRichTextBlock),
            new PropertyMetadata(true, OnShowLineNumbersChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string SearchText
    {
        get => (string)GetValue(SearchTextProperty);
        set => SetValue(SearchTextProperty, value);
    }

    public bool WordWrap
    {
        get => (bool)GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public bool ShowLineNumbers
    {
        get => (bool)GetValue(ShowLineNumbersProperty);
        set => SetValue(ShowLineNumbersProperty, value);
    }

    public JsonRichTextBlock()
    {
        _statusText = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
            VerticalAlignment = VerticalAlignment.Center
        };

        _wrapToggle = new ToggleButton
        {
            Content = "Wrap",
            IsChecked = true,
            FontSize = 11,
            Padding = new Thickness(8, 3, 8, 3),
            MinHeight = 0,
            MinWidth = 0
        };
        _wrapToggle.Checked += (_, _) => WordWrap = true;
        _wrapToggle.Unchecked += (_, _) => WordWrap = false;

        var selectAllButton = CreateToolbarButton("\uE8B3", "Select all");
        selectAllButton.Click += (_, _) => SelectAll();

        var copyButton = CreateToolbarButton("\uE8C8", "Copy all");
        copyButton.Click += (_, _) => CopyText(_renderedText);

        var toolbar = new Grid { ColumnSpacing = 8 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.Children.Add(_statusText);
        Grid.SetColumn(_wrapToggle, 1);
        toolbar.Children.Add(_wrapToggle);
        Grid.SetColumn(selectAllButton, 2);
        toolbar.Children.Add(selectAllButton);
        Grid.SetColumn(copyButton, 3);
        toolbar.Children.Add(copyButton);

        _lineNumbers = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 115, 115, 115)),
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(0, 8, 8, 8),
            IsTextSelectionEnabled = false
        };

        _lineNumberScrollViewer = new ScrollViewer
        {
            Content = _lineNumbers,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            IsTabStop = false,
            Width = 58
        };

        _editor = new RichEditBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _editor.Loaded += OnEditorLoaded;

        var body = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255))
        };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_lineNumberScrollViewer);
        Grid.SetColumn(_editor, 1);
        body.Children.Add(_editor);

        var root = new Grid { RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(toolbar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Content = root;
        RenderAsync();
    }

    private static Button CreateToolbarButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe Fluent Icons")
            },
            Padding = new Thickness(8, 3, 8, 3),
            MinHeight = 0,
            MinWidth = 0
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JsonRichTextBlock control)
            control.RenderAsync();
    }

    private static void OnSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JsonRichTextBlock control)
            control.ApplyFormatting();
    }

    private static void OnWordWrapChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JsonRichTextBlock control)
        {
            control._wrapToggle.IsChecked = control.WordWrap;
            control._editor.TextWrapping = control.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }
    }

    private static void OnShowLineNumbersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JsonRichTextBlock control)
            control._lineNumberScrollViewer.Visibility = control.ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderAsync()
    {
        var version = Interlocked.Increment(ref _renderVersion);
        var text = Text ?? string.Empty;
        _statusText.Text = string.IsNullOrEmpty(text) ? "0 lines, 0 chars" : "Preparing...";

        _ = Task.Run(() => PrepareDocument(text))
            .ContinueWith(task =>
            {
                _ = DispatcherQueue.TryEnqueue(() =>
                {
                    if (version != _renderVersion)
                        return;

                    if (task.IsFaulted)
                    {
                        _renderedText = string.Empty;
                        _tokens = [];
                        SetDocumentText(string.Empty);
                        _lineNumbers.Text = string.Empty;
                        _statusText.Text = "Failed to render text";
                        return;
                    }

                    var model = task.Result;
                    _renderedText = model.RenderedText;
                    _tokens = model.Tokens;
                    _lineNumbers.Text = model.LineNumbers;
                    _statusText.Text = model.StatusText;
                    _lineNumberScrollViewer.Visibility = ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
                    _editor.TextWrapping = WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    SetDocumentText(_renderedText);
                    ApplyFormatting();
                });
            }, TaskScheduler.Default);
    }

    private void SetDocumentText(string text)
    {
        var wasReadOnly = _editor.IsReadOnly;
        _editor.IsReadOnly = false;
        try
        {
            _editor.Document.SetText(TextSetOptions.None, text);
        }
        finally
        {
            _editor.IsReadOnly = wasReadOnly;
        }
    }

    private void ApplyFormatting()
    {
        if (string.IsNullOrEmpty(_renderedText))
            return;

        var wasReadOnly = _editor.IsReadOnly;
        _editor.IsReadOnly = false;
        var document = _editor.Document;
        document.BatchDisplayUpdates();
        try
        {
            var all = document.GetRange(0, _renderedText.Length);
            all.CharacterFormat.ForegroundColor = TextColor;
            all.CharacterFormat.BackgroundColor = Colors.Transparent;
            all.CharacterFormat.Bold = FormatEffect.Off;

            foreach (var token in _tokens)
            {
                if (token.Start >= _renderedText.Length) continue;
                var end = Math.Min(_renderedText.Length, token.Start + token.Length);
                if (end <= token.Start) continue;

                var range = document.GetRange(token.Start, end);
                range.CharacterFormat.ForegroundColor = token.Kind switch
                {
                    SyntaxKind.Key => KeyColor,
                    SyntaxKind.String => StringColor,
                    SyntaxKind.Number => NumberColor,
                    SyntaxKind.Bool => BoolColor,
                    SyntaxKind.Null => NullColor,
                    SyntaxKind.Punctuation => PunctuationColor,
                    _ => TextColor
                };
            }

            var search = SearchText?.Trim();
            if (!string.IsNullOrEmpty(search))
            {
                var start = 0;
                while (start < _renderedText.Length)
                {
                    var match = _renderedText.IndexOf(search, start, StringComparison.OrdinalIgnoreCase);
                    if (match < 0) break;

                    var range = document.GetRange(match, match + search.Length);
                    range.CharacterFormat.BackgroundColor = SearchBackgroundColor;
                    range.CharacterFormat.Bold = FormatEffect.On;
                    start = match + search.Length;
                }
            }
        }
        finally
        {
            document.ApplyDisplayUpdates();
            _editor.IsReadOnly = wasReadOnly;
        }
    }

    private void SelectAll()
    {
        _editor.Focus(FocusState.Programmatic);
        _editor.Document.Selection.SetRange(0, _renderedText.Length);
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (_editorScrollViewer != null)
            return;

        _editorScrollViewer = FindDescendant<ScrollViewer>(_editor);
        if (_editorScrollViewer != null)
        {
            _editorScrollViewer.ViewChanged += (_, _) =>
                _lineNumberScrollViewer.ChangeView(null, _editorScrollViewer.VerticalOffset, null, disableAnimation: true);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var descendant = FindDescendant<T>(child);
            if (descendant != null) return descendant;
        }

        return null;
    }

    private static PreparedJsonText PrepareDocument(string text)
    {
        var originalChars = text.Length;
        var truncated = false;
        if (text.Length > MaxViewerChars)
        {
            text = text[..MaxViewerChars]
                   + $"{Environment.NewLine}{Environment.NewLine}... [viewer truncated, original was {originalChars:N0} chars]";
            truncated = true;
        }

        var looksJson = LooksLikeJson(text);
        var rendered = looksJson && text.Length <= MaxPrettyPrintChars
            ? PrettyPrintJsonLike(text)
            : NormalizeLineEndings(text);

        var lineCount = CountLines(rendered);
        if (lineCount > MaxLines)
        {
            rendered = TruncateLines(rendered, MaxLines, lineCount);
            lineCount = MaxLines;
            truncated = true;
        }

        var tokens = looksJson ? Tokenize(rendered) : [];
        if (tokens.Count > MaxFormattedTokens)
        {
            tokens = tokens[..MaxFormattedTokens];
            truncated = true;
        }

        var status = $"{lineCount:N0} lines, {originalChars:N0} chars";
        if (looksJson) status += ", JSON";
        if (truncated) status += ", truncated";

        return new PreparedJsonText(rendered, BuildLineNumbers(lineCount), tokens, status);
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[';
    }

    private static string PrettyPrintJsonLike(string text)
    {
        text = NormalizeLineEndings(text);
        var sb = new StringBuilder(text.Length + Math.Min(text.Length / 2, 32_768));
        var indent = 0;
        var inString = false;
        var escaped = false;

        foreach (var c in text)
        {
            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    sb.Append(c);
                    break;
                case '{':
                case '[':
                    sb.Append(c);
                    AppendNewLine(sb, ++indent);
                    break;
                case '}':
                case ']':
                    AppendNewLine(sb, Math.Max(0, --indent));
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(c);
                    AppendNewLine(sb, indent);
                    break;
                case ':':
                    sb.Append(": ");
                    break;
                case '\n':
                case '\t':
                case ' ':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    private static void AppendNewLine(StringBuilder sb, int indent)
    {
        sb.Append('\n');
        sb.Append(' ', indent * 2);
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n') count++;
        }

        return count;
    }

    private static string TruncateLines(string text, int maxLines, int originalLineCount)
    {
        var seen = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            seen++;
            if (seen >= maxLines - 1)
                return text[..i] + $"\n... [viewer truncated, original had {originalLineCount:N0} lines]";
        }

        return text;
    }

    private static string BuildLineNumbers(int lineCount)
    {
        if (lineCount <= 0) return string.Empty;

        var sb = new StringBuilder(lineCount * 6);
        for (var i = 1; i <= lineCount; i++)
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    private static List<SyntaxToken> Tokenize(string json)
    {
        var tokens = new List<SyntaxToken>();
        var i = 0;
        while (i < json.Length)
        {
            var c = json[i];

            if (c is ' ' or '\t' or '\n')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                var start = i;
                ReadString(json, ref i);
                var kind = SkipWhitespaceAndCheck(json, i, ':') ? SyntaxKind.Key : SyntaxKind.String;
                tokens.Add(new SyntaxToken(start, i - start, kind));
                continue;
            }

            if (c is '-' or (>= '0' and <= '9'))
            {
                var start = i;
                if (c == '-') i++;
                while (i < json.Length && json[i] is (>= '0' and <= '9') or '.' or 'e' or 'E' or '+' or '-')
                    i++;
                tokens.Add(new SyntaxToken(start, i - start, SyntaxKind.Number));
                continue;
            }

            if (TryReadKeyword(json, i, "true", out var keyword) ||
                TryReadKeyword(json, i, "false", out keyword))
            {
                tokens.Add(new SyntaxToken(i, keyword!.Length, SyntaxKind.Bool));
                i += keyword.Length;
                continue;
            }

            if (TryReadKeyword(json, i, "null", out keyword))
            {
                tokens.Add(new SyntaxToken(i, keyword!.Length, SyntaxKind.Null));
                i += keyword.Length;
                continue;
            }

            if (c is '{' or '}' or '[' or ']' or ',' or ':')
            {
                tokens.Add(new SyntaxToken(i, 1, SyntaxKind.Punctuation));
            }

            i++;
        }

        return tokens;
    }

    private static void ReadString(string json, ref int i)
    {
        i++;
        while (i < json.Length)
        {
            if (json[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (json[i] == '"')
            {
                i++;
                break;
            }

            i++;
        }
    }

    private static bool SkipWhitespaceAndCheck(string json, int i, char expected)
    {
        while (i < json.Length && json[i] is ' ' or '\t' or '\n') i++;
        return i < json.Length && json[i] == expected;
    }

    private static bool TryReadKeyword(string json, int i, string keyword, out string? result)
    {
        result = null;
        if (i + keyword.Length > json.Length) return false;
        for (var j = 0; j < keyword.Length; j++)
        {
            if (json[i + j] != keyword[j]) return false;
        }

        var end = i + keyword.Length;
        if (end < json.Length && char.IsLetterOrDigit(json[end])) return false;
        result = keyword;
        return true;
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private sealed record PreparedJsonText(
        string RenderedText,
        string LineNumbers,
        IReadOnlyList<SyntaxToken> Tokens,
        string StatusText);

    private readonly record struct SyntaxToken(int Start, int Length, SyntaxKind Kind);

    private enum SyntaxKind
    {
        Key,
        String,
        Number,
        Bool,
        Null,
        Punctuation,
    }
}
