using System;
using System.Collections.Generic;

namespace Wavee.Core;

/// <summary>One Liked Songs content-filter chip as the server defines it: a presentation <see cref="Title"/> plus the
/// lowercase descriptor <see cref="Token"/> its query matches against.</summary>
/// <param name="Title">Presentation form, e.g. "Mellow". Already localized per market — never re-localize it.</param>
/// <param name="Token">The lowercase match token from <c>tags contains &lt;token&gt;</c>, e.g. "mellow".</param>
public sealed record ContentFilterChip(string Title, string Token);

/// <summary>Parses the <c>content-filter/v1/liked-songs</c> body.
///
/// Pure and engine-free so the shape is unit-testable without a network. The corpus contains only a 304 for this
/// endpoint, so the 200 body shape is documented rather than wire-verified — which is precisely why the parser is
/// STRICT and total: anything it does not positively recognise is dropped, and a body it cannot use at all yields an
/// empty list so the caller falls back to deriving chips from the tracks instead of showing something wrong.</summary>
public static class ContentFilterParser
{
    /// <summary>The only query form Spotify is known to emit, and the only one with a defined client-side meaning.
    /// A chip carrying any other query is dropped rather than guessed at — a chip that filters incorrectly is worse
    /// than a chip that is absent.</summary>
    const string TagsContains = "tags contains ";

    /// <summary>Extracts the usable chips. Never throws: a malformed body is an empty result, not an exception.</summary>
    public static IReadOnlyList<ContentFilterChip> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ContentFilterChip>();

        List<ContentFilterChip>? chips = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return Array.Empty<ContentFilterChip>();
            if (!doc.RootElement.TryGetProperty("contentFilters", out var arr)
                || arr.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Array.Empty<ContentFilterChip>();

            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                string? title = Str(e, "title");
                string? query = Str(e, "query");
                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(query)) continue;

                string? token = TokenOf(query!);
                if (token is null || !seen.Add(token)) continue;   // unsupported query form, or a duplicate concept
                (chips ??= new List<ContentFilterChip>()).Add(new ContentFilterChip(title!, token));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<ContentFilterChip>();
        }
        return (IReadOnlyList<ContentFilterChip>?)chips ?? Array.Empty<ContentFilterChip>();
    }

    /// <summary>The token from <c>tags contains &lt;token&gt;</c>, or null when the query is any other form.</summary>
    public static string? TokenOf(string query)
    {
        var q = query.AsSpan().Trim();
        if (!q.StartsWith(TagsContains, StringComparison.OrdinalIgnoreCase)) return null;
        var token = q[TagsContains.Length..].Trim();
        // Spotify has been observed sending the token bare; tolerate quoting rather than emitting a token with quotes
        // baked in, which would never match a descriptor.
        if (token.Length >= 2 && (token[0] == '"' || token[0] == '\'') && token[^1] == token[0])
            token = token[1..^1].Trim();
        return token.Length == 0 ? null : token.ToString();
    }

    static string? Str(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
}
