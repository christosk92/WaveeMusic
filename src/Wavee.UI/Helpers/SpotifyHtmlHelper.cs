using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Wavee.UI.Helpers;

/// <summary>
/// One token of a parsed Spotify description: either a plain-text run or a Spotify
/// link. Consumers walk an ordered sequence of these to materialize a RichTextBlock
/// with mixed Run + Hyperlink inlines while preserving the original ordering.
/// </summary>
internal readonly record struct SpotifyHtmlToken(bool IsLink, string Text, string? Uri);

/// <summary>
/// Utilities for parsing Spotify's HTML-formatted descriptions.
/// Spotify uses &lt;a href=spotify:playlist:xxx&gt;Name&lt;/a&gt; format in descriptions.
/// </summary>
internal static partial class SpotifyHtmlHelper
{
    /// <summary>
    /// Strips all HTML tags, returning plain text. Converts
    /// &lt;a href=...&gt;text&lt;/a&gt; to just "text" and &lt;br&gt; to a
    /// newline. HTML entities (&amp;amp;, &amp;quot;, &amp;#39;, &amp;#x1f90d;,
    /// …) are decoded. Returns <c>null</c> when the input is null or empty so
    /// callers can use the null-coalesce pattern naturally.
    /// </summary>
    public static string? StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var text = BrTagRegex().Replace(html, "\n");
        text = HtmlTagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return text.Trim();
    }

    /// <summary>
    /// Extracts Spotify link references from HTML descriptions.
    /// Returns a list of (displayText, spotifyUri) tuples.
    /// </summary>
    public static List<(string Text, string Uri)> ExtractLinks(string? html)
    {
        var links = new List<(string Text, string Uri)>();
        if (string.IsNullOrEmpty(html)) return links;

        foreach (Match match in SpotifyLinkRegex().Matches(html))
        {
            var spotifyUri = match.Groups[1].Value;
            var text = match.Groups[2].Value;
            links.Add((text, spotifyUri));
        }

        return links;
    }

    /// <summary>
    /// Walks the description in source order, yielding plain-text runs interleaved
    /// with the embedded Spotify links. Empty text segments between adjacent links
    /// are skipped. HTML entities (&amp;amp;, &amp;quot;, etc.) are decoded inside
    /// each text run; tags other than &lt;a&gt; are stripped from text segments
    /// (matches the existing StripHtml behavior so we don't accidentally surface
    /// raw markup if Spotify ever embeds &lt;br&gt; or similar).
    /// </summary>
    public static IReadOnlyList<SpotifyHtmlToken> Tokenize(string? html)
    {
        var tokens = new List<SpotifyHtmlToken>();
        if (string.IsNullOrEmpty(html)) return tokens;

        int cursor = 0;
        foreach (Match match in SpotifyLinkRegex().Matches(html))
        {
            if (match.Index > cursor)
            {
                var pre = html.Substring(cursor, match.Index - cursor);
                AppendText(tokens, pre);
            }

            var uri = match.Groups[1].Value;
            var text = match.Groups[2].Value;
            // Link's display text can itself contain entities (&amp; etc.) — decode.
            tokens.Add(new SpotifyHtmlToken(IsLink: true, Text: WebUtility.HtmlDecode(text), Uri: uri));
            cursor = match.Index + match.Length;
        }

        if (cursor < html.Length)
            AppendText(tokens, html.Substring(cursor));

        return tokens;
    }

    private static void AppendText(List<SpotifyHtmlToken> tokens, string segment)
    {
        // Strip any non-link HTML tags out of the surrounding text the same way
        // StripHtml does, then decode entities. Skip if the result is empty so we
        // don't emit no-op runs that the renderer would have to filter out.
        var stripped = HtmlTagRegex().Replace(segment, "");
        var decoded = WebUtility.HtmlDecode(stripped);
        if (string.IsNullOrEmpty(decoded)) return;
        tokens.Add(new SpotifyHtmlToken(IsLink: false, Text: decoded, Uri: null));
    }

    // Matches any HTML tag
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    // Matches <br>, <br/>, <br />, case-insensitive — replaced with newline
    // before the generic tag-strip so paragraph breaks survive.
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BrTagRegex();

    // Matches <a href=spotify:...>text</a> or <a href="spotify:...">text</a>
    [GeneratedRegex(@"<a\s+href=""?(spotify:[^"">\s]+)""?[^>]*>(.*?)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyLinkRegex();
}
