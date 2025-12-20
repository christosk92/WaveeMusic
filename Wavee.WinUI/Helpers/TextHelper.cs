using System.Net;
using System.Text.RegularExpressions;

namespace Wavee.WinUI.Helpers;

/// <summary>
/// Helper class for text processing and formatting
/// </summary>
public static class TextHelper
{
    /// <summary>
    /// Strips HTML tags and decodes HTML entities from a string
    /// </summary>
    /// <param name="html">HTML-formatted text</param>
    /// <returns>Plain text with HTML tags removed and entities decoded</returns>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        // Remove HTML tags using regex
        var withoutTags = Regex.Replace(html, "<.*?>", string.Empty);

        // Decode HTML entities (&amp; -> &, &lt; -> <, etc.)
        var decoded = WebUtility.HtmlDecode(withoutTags);

        // Normalize whitespace and trim
        var normalized = Regex.Replace(decoded, @"\s+", " ");

        return normalized.Trim();
    }
}
