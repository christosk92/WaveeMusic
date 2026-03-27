using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Wavee.UI.WinUI.Controls.Omnibar;

/// <summary>
/// A TextBlock that bolds the portion of Text matching MatchText.
/// e.g. Text="arabian nights", MatchText="ara" → "<b>ara</b>bian nights"
/// </summary>
public sealed class BoldMatchTextBlock : Control
{
    private RichTextBlock? _rtb;

    public BoldMatchTextBlock()
    {
        DefaultStyleKey = typeof(BoldMatchTextBlock);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rtb = GetTemplateChild("PART_RichTextBlock") as RichTextBlock;
        UpdateText();
    }

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(BoldMatchTextBlock),
            new PropertyMetadata(null, OnPropertyChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static readonly DependencyProperty MatchTextProperty =
        DependencyProperty.Register(nameof(MatchText), typeof(string), typeof(BoldMatchTextBlock),
            new PropertyMetadata(null, OnPropertyChanged));

    public string? MatchText
    {
        get => (string?)GetValue(MatchTextProperty);
        set => SetValue(MatchTextProperty, value);
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BoldMatchTextBlock ctrl) ctrl.UpdateText();
    }

    private void UpdateText()
    {
        if (_rtb == null) return;

        _rtb.Blocks.Clear();
        var text = Text ?? "";
        var match = MatchText ?? "";
        var paragraph = new Paragraph();

        if (!string.IsNullOrEmpty(match))
        {
            var idx = text.IndexOf(match, System.StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                if (idx > 0)
                    paragraph.Inlines.Add(new Run { Text = text[..idx] });

                paragraph.Inlines.Add(new Run
                {
                    Text = text.Substring(idx, match.Length),
                    FontWeight = FontWeights.Bold
                });

                var after = idx + match.Length;
                if (after < text.Length)
                    paragraph.Inlines.Add(new Run { Text = text[after..] });
            }
            else
            {
                paragraph.Inlines.Add(new Run { Text = text });
            }
        }
        else
        {
            paragraph.Inlines.Add(new Run { Text = text });
        }

        _rtb.Blocks.Add(paragraph);
    }
}
