using System;
using Wavee.Core.Http;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// UI-facing liked-songs filter definition derived from Spotify content filters.
/// </summary>
public sealed record LikedSongsFilterDto
{
    private const string TagsContainsPrefix = "tags contains ";

    public string Title { get; init; } = "";

    public string Query { get; init; } = "";

    public string? TagValue { get; init; }

    public bool IsSupported => !string.IsNullOrWhiteSpace(TagValue);

    public static LikedSongsFilterDto FromContentFilter(LikedSongsContentFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return new LikedSongsFilterDto
        {
            Title = filter.Title,
            Query = filter.Query,
            TagValue = TryExtractTagValue(filter.Query)
        };
    }

    private static string? TryExtractTagValue(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        var trimmed = query.Trim();
        if (!trimmed.StartsWith(TagsContainsPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var value = trimmed[TagsContainsPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
