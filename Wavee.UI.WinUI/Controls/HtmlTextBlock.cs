using System;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Wavee.UI.WinUI.Helpers.Navigation;

namespace Wavee.UI.WinUI.Controls;

/// <summary>
/// Renders HTML text as a RichTextBlock with clickable Spotify URI links.
/// Decodes HTML entities, converts &lt;a href="spotify:..."&gt; to in-app navigation,
/// and preserves line breaks.
/// </summary>
public sealed partial class HtmlTextBlock : UserControl
{
    private readonly RichTextBlock _richTextBlock;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(HtmlTextBlock),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty MaxLinesProperty =
        DependencyProperty.Register(nameof(MaxLines), typeof(int), typeof(HtmlTextBlock),
            new PropertyMetadata(0, OnMaxLinesChanged));

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int MaxLines
    {
        get => (int)GetValue(MaxLinesProperty);
        set => SetValue(MaxLinesProperty, value);
    }

    public HtmlTextBlock()
    {
        _richTextBlock = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsTextSelectionEnabled = true
        };
        Content = _richTextBlock;
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HtmlTextBlock control) control.RenderHtml();
    }

    private static void OnMaxLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HtmlTextBlock control)
            control._richTextBlock.MaxLines = (int)e.NewValue;
    }

    private void RenderHtml()
    {
        _richTextBlock.Blocks.Clear();

        var html = Text;
        if (string.IsNullOrEmpty(html)) return;

        html = WebUtility.HtmlDecode(html);

        var paragraph = new Paragraph();
        var lines = html.Split('\n');

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            if (lineIdx > 0)
                paragraph.Inlines.Add(new LineBreak());

            var line = lines[lineIdx].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var pos = 0;
            foreach (Match match in LinkRegex().Matches(line))
            {
                if (match.Index > pos)
                {
                    var before = StripTags(line[pos..match.Index]);
                    if (!string.IsNullOrEmpty(before))
                        paragraph.Inlines.Add(new Run { Text = before });
                }

                var href = match.Groups[1].Value;
                var linkText = WebUtility.HtmlDecode(StripTags(match.Groups[2].Value));

                if (href.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
                {
                    var hyperlink = new Hyperlink();
                    hyperlink.Inlines.Add(new Run { Text = linkText });
                    hyperlink.UnderlineStyle = UnderlineStyle.None;
                    var uri = href;
                    hyperlink.Click += (_, _) => NavigateSpotifyUri(uri, linkText);
                    paragraph.Inlines.Add(hyperlink);
                }
                else
                {
                    paragraph.Inlines.Add(new Run { Text = linkText });
                }

                pos = match.Index + match.Length;
            }

            if (pos < line.Length)
            {
                var remaining = StripTags(line[pos..]);
                if (!string.IsNullOrEmpty(remaining))
                    paragraph.Inlines.Add(new Run { Text = remaining });
            }
        }

        _richTextBlock.Blocks.Add(paragraph);

        if (MaxLines > 0)
            _richTextBlock.MaxLines = MaxLines;
    }

    private static void NavigateSpotifyUri(string uri, string title)
    {
        var parts = uri.Split(':');
        if (parts.Length < 3) return;

        var param = new Data.Parameters.ContentNavigationParameter { Uri = uri, Title = title };

        switch (parts[1])
        {
            case "artist": NavigationHelpers.OpenArtist(param, title); break;
            case "album": NavigationHelpers.OpenAlbum(param, title); break;
            case "playlist": NavigationHelpers.OpenPlaylist(param, title); break;
        }
    }

    private static string StripTags(string html) => TagRegex().Replace(html, "");

    [GeneratedRegex(@"<a\s+href=""?([^"">\s]+)""?[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
