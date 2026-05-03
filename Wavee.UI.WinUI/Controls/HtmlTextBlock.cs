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

        var matches = BlockRegex().Matches(html);
        if (matches.Count == 0)
        {
            AddParagraph(html, isBullet: false);
        }
        else
        {
            var cursor = 0;
            foreach (Match match in matches)
            {
                if (match.Index > cursor)
                    AddParagraph(html[cursor..match.Index], isBullet: false);

                var isBullet = match.Groups["li"].Success;
                var fragment = isBullet ? match.Groups["li"].Value : match.Groups["p"].Value;
                AddParagraph(fragment, isBullet);
                cursor = match.Index + match.Length;
            }

            if (cursor < html.Length)
                AddParagraph(html[cursor..], isBullet: false);
        }

        if (MaxLines > 0)
            _richTextBlock.MaxLines = MaxLines;
    }

    private void AddParagraph(string htmlFragment, bool isBullet)
    {
        htmlFragment = NormalizeBlockHtml(htmlFragment);
        if (string.IsNullOrWhiteSpace(htmlFragment)) return;

        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, isBullet ? 4 : 12)
        };

        if (isBullet)
            paragraph.Inlines.Add(new Run { Text = "• " });

        var lines = htmlFragment.Split('\n');
        var wroteContent = false;

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (wroteContent)
                paragraph.Inlines.Add(new LineBreak());

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
                else if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
                {
                    paragraph.Inlines.Add(new Hyperlink
                    {
                        NavigateUri = uri,
                        UnderlineStyle = UnderlineStyle.None,
                        Inlines = { new Run { Text = linkText } }
                    });
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

            wroteContent = true;
        }

        if (wroteContent)
            _richTextBlock.Blocks.Add(paragraph);
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

    private static string NormalizeBlockHtml(string html)
    {
        html = BlockBreakRegex().Replace(html, "\n");
        html = ParagraphBreakRegex().Replace(html, "\n");
        html = ListContainerRegex().Replace(html, "");
        html = NonAnchorTagRegex().Replace(html, "");
        html = WhitespaceRegex().Replace(html, " ");
        html = NewlineWhitespaceRegex().Replace(html, "\n");
        return html.Trim();
    }

    [GeneratedRegex(@"<li\b[^>]*>(?<li>.*?)</li>|<p\b[^>]*>(?<p>.*?)</p>|<div\b[^>]*>(?<p>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<a\s+href=""?([^"">\s]+)""?[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"</(p|div|li|h[1-6])>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphBreakRegex();

    [GeneratedRegex(@"</?(ul|ol)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListContainerRegex();

    [GeneratedRegex(@"<(?!/?a\b)[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex NonAnchorTagRegex();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\s*\n\s*")]
    private static partial Regex NewlineWhitespaceRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();
}
