using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.DTOs;

/// <summary>
/// Flattened, view-friendly snapshot of a Spotify podcast show. Built by
/// PodcastService from the queryShowMetadataV2 GraphQL response — the page
/// binds against this rather than the raw Pathfinder DTO so the VM doesn't
/// have to care which fields are nested under which wrapper.
/// </summary>
public sealed class ShowDetailDto
{
    public string Uri { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? PublisherName { get; init; }
    public string? PlainDescription { get; init; }
    public string? CoverArtUrl { get; init; }

    /// <summary>"MIXED" / "AUDIO" / "VIDEO" — surfaced for video badge UX.</summary>
    public string? MediaType { get; init; }

    public bool IsExplicit { get; init; }
    public bool IsExclusive { get; init; }
    public bool IsPlayable { get; init; }
    public bool IsSavedOnServer { get; init; }
    public string? ConsumptionOrder { get; init; }
    public string? ShareUrl { get; init; }
    public string? TrailerUri { get; init; }

    public double AverageRating { get; init; }
    public bool ShowAverageRating { get; init; }
    public long TotalRatings { get; init; }

    public IReadOnlyList<ShowTopicDto> Topics { get; init; } = [];
    public IReadOnlyList<string> EpisodeUris { get; init; } = [];
    public int TotalEpisodes { get; init; }

    /// <summary>Palette derived from <c>visualIdentity.squareCoverImage</c>.</summary>
    public ShowPaletteDto? Palette { get; init; }
}

public sealed class ShowTopicDto
{
    public string Title { get; init; } = "";
    public string? Uri { get; init; }
}

public sealed class ShowPaletteDto
{
    public ShowPaletteTier? HighContrast { get; init; }
    public ShowPaletteTier? HigherContrast { get; init; }
    public ShowPaletteTier? MinContrast { get; init; }
}

public sealed class ShowPaletteTier
{
    public byte BackgroundR { get; init; }
    public byte BackgroundG { get; init; }
    public byte BackgroundB { get; init; }

    public byte BackgroundTintedR { get; init; }
    public byte BackgroundTintedG { get; init; }
    public byte BackgroundTintedB { get; init; }

    public byte TextAccentR { get; init; }
    public byte TextAccentG { get; init; }
    public byte TextAccentB { get; init; }
}
