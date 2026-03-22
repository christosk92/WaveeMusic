using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Wavee.UI.WinUI.Helpers;

/// <summary>
/// Utilities for parsing Spotify's HTML-formatted descriptions.
/// Spotify uses &lt;a href=spotify:playlist:xxx&gt;Name&lt;/a&gt; format in descriptions.
/// </summary>
public static partial class SpotifyHtmlHelper
{
    /// <summary>
    /// Strips all HTML tags, returning plain text.
    /// Converts &lt;a href=...&gt;text&lt;/a&gt; to just "text".
    /// </summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        return HtmlTagRegex().Replace(html, "").Trim();
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

    // Matches any HTML tag
    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    // Matches <a href=spotify:...>text</a> or <a href="spotify:...">text</a>
    [GeneratedRegex(@"<a\s+href=""?(spotify:[^"">\s]+)""?[^>]*>(.*?)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SpotifyLinkRegex();
}
