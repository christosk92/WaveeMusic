using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.Models;

public sealed class ChangelogRelease
{
    public required string Version { get; init; }
    public required string ReleaseTitle { get; init; }
    public required IReadOnlyList<ChangelogFeature> Features { get; init; }

    /// <summary>
    /// Optional developer announcement shown at the top of the dialog.
    /// </summary>
    public string? Announcement { get; init; }

    /// <summary>
    /// Optional link to the GitHub release page for full changelog.
    /// </summary>
    public string? ReleaseUrl { get; init; }
}

public sealed class ChangelogFeature
{
    public required string Title { get; init; }
    public required string ShortDescription { get; init; }
    public required string Glyph { get; init; }
    public required string DetailTitle { get; init; }
    public required string DetailDescription { get; init; }
    public string? ImageAssetPath { get; init; }
}
