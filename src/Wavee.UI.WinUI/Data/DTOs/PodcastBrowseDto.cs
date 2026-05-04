using System.Collections.Generic;
using System.Linq;

namespace Wavee.UI.WinUI.Data.DTOs;

public enum PodcastBrowseItemKind
{
    Unknown,
    Category,
    Section,
    Show
}

public sealed record PodcastBrowsePageDto
{
    public string Uri { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? HeaderColorHex { get; init; }
    public IReadOnlyList<PodcastBrowseSectionDto> Sections { get; init; } = [];

    public IEnumerable<PodcastBrowseSectionDto> ShowSections =>
        Sections.Where(static section => section.Items.Any(static item => item.Kind == PodcastBrowseItemKind.Show));

    public IEnumerable<PodcastBrowseSectionDto> CategorySections =>
        Sections.Where(static section => section.Items.Any(static item => item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section));
}

public sealed record PodcastBrowseSectionDto
{
    public string Uri { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? TypeName { get; init; }
    public IReadOnlyList<PodcastBrowseItemDto> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int? NextOffset { get; init; }

    public bool HasMore => NextOffset.HasValue;
    public bool HasShows => Items.Any(static item => item.Kind == PodcastBrowseItemKind.Show);
    public bool HasCategories => Items.Any(static item => item.Kind is PodcastBrowseItemKind.Category or PodcastBrowseItemKind.Section);
}

public sealed record PodcastBrowseItemDto
{
    public string Uri { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public string? ColorHex { get; init; }
    public PodcastBrowseItemKind Kind { get; init; }
    public string? MediaType { get; init; }
    public string? SourceLabel { get; init; }
}
