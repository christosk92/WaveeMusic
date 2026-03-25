using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Renders text as a RichTextBlock with JSON syntax coloring.
/// Falls back to plain monospace for non-JSON content.
/// </summary>
public sealed partial class JsonRichTextBlock : UserControl
{
    private readonly RichTextBlock _richTextBlock;

    // Theme-aware color palettes
    private static readonly SolidColorBrush KeyBrush = new(Color.FromArgb(255, 86, 156, 214));     // blue
    private static readonly SolidColorBrush StringBrush = new(Color.FromArgb(255, 206, 145, 120));  // orange
    private static readonly SolidColorBrush NumberBrush = new(Color.FromArgb(255, 181, 206, 168));  // green
    private static readonly SolidColorBrush BoolBrush = new(Color.FromArgb(255, 86, 156, 214));     // blue
    private static readonly SolidColorBrush NullBrush = new(Color.FromArgb(255, 128, 128, 128));    // gray
    private static readonly SolidColorBrush PuncBrush = new(Color.FromArgb(255, 150, 150, 150));    // light gray

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(JsonRichTextBlock),
            new PropertyMetadata(null, OnTextChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public JsonRichTextBlock()
    {
        _richTextBlock = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = 12
        };
        Content = _richTextBlock;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JsonRichTextBlock control) control.Render();
    }

    private void Render()
    {
        _richTextBlock.Blocks.Clear();
        var text = Text;
        if (string.IsNullOrEmpty(text)) return;

        // Detect JSON: starts with { or [
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            RenderJson(text);
        }
        else
        {
            RenderPlain(text);
        }
    }

    private void RenderPlain(string text)
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run { Text = text });
        _richTextBlock.Blocks.Add(paragraph);
    }

    private void RenderJson(string json)
    {
        var paragraph = new Paragraph();
        var i = 0;
        var len = json.Length;

        while (i < len)
        {
            var c = json[i];

            // Whitespace (preserve formatting)
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                var start = i;
                while (i < len && json[i] is ' ' or '\t' or '\r' or '\n') i++;
                paragraph.Inlines.Add(new Run { Text = json[start..i] });
                continue;
            }

            // Strings
            if (c == '"')
            {
                var str = ReadString(json, ref i);
                var isKey = SkipWhitespaceAndCheck(json, i, ':');
                paragraph.Inlines.Add(new Run
                {
                    Text = str,
                    Foreground = isKey ? KeyBrush : StringBrush
                });
                continue;
            }

            // Numbers
            if (c is '-' or (>= '0' and <= '9'))
            {
                var start = i;
                if (c == '-') i++;
                while (i < len && json[i] is (>= '0' and <= '9') or '.' or 'e' or 'E' or '+' or '-')
                    i++;
                paragraph.Inlines.Add(new Run
                {
                    Text = json[start..i],
                    Foreground = NumberBrush
                });
                continue;
            }

            // Keywords: true, false, null
            if (TryReadKeyword(json, i, "true", out var kw)
                || TryReadKeyword(json, i, "false", out kw))
            {
                paragraph.Inlines.Add(new Run { Text = kw, Foreground = BoolBrush });
                i += kw!.Length;
                continue;
            }
            if (TryReadKeyword(json, i, "null", out kw))
            {
                paragraph.Inlines.Add(new Run { Text = kw, Foreground = NullBrush });
                i += kw!.Length;
                continue;
            }

            // Punctuation: { } [ ] , :
            if (c is '{' or '}' or '[' or ']' or ',' or ':')
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = c.ToString(),
                    Foreground = PuncBrush
                });
                i++;
                continue;
            }

            // Anything else — just emit as-is
            paragraph.Inlines.Add(new Run { Text = c.ToString() });
            i++;
        }

        _richTextBlock.Blocks.Add(paragraph);
    }

    private static string ReadString(string json, ref int i)
    {
        var start = i;
        i++; // skip opening quote
        while (i < json.Length)
        {
            if (json[i] == '\\') { i += 2; continue; }
            if (json[i] == '"') { i++; break; }
            i++;
        }
        return json[start..i];
    }

    private static bool SkipWhitespaceAndCheck(string json, int i, char expected)
    {
        while (i < json.Length && json[i] is ' ' or '\t' or '\r' or '\n') i++;
        return i < json.Length && json[i] == expected;
    }

    private static bool TryReadKeyword(string json, int i, string keyword, out string? result)
    {
        result = null;
        if (i + keyword.Length > json.Length) return false;
        for (int j = 0; j < keyword.Length; j++)
        {
            if (json[i + j] != keyword[j]) return false;
        }
        // Make sure it's not part of a longer identifier
        var end = i + keyword.Length;
        if (end < json.Length && char.IsLetterOrDigit(json[end])) return false;
        result = keyword;
        return true;
    }
}
