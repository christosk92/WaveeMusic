using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.Models;

// `required` is intentionally NOT used on these models. The WinUI XAML type-
// info generator emits a parameterless `new ChangelogFeature()` for every
// `x:DataType` registration, which fails to compile when the type has
// `required` members. Defaults (`= ""`) keep nullability happy while still
// surfacing missing data visibly at runtime if a release entry is incomplete.
public sealed class ChangelogRelease
{
    public string Version { get; init; } = "";
    public string ReleaseTitle { get; init; } = "";
    public IReadOnlyList<ChangelogFeature> Features { get; init; } = [];

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
    public string Title { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string Glyph { get; set; } = "";
    public string DetailTitle { get; set; } = "";
    public string DetailDescription { get; set; } = "";
    public string? NavigationHint { get; set; }
    public string? ImageAssetPath { get; set; }
}
