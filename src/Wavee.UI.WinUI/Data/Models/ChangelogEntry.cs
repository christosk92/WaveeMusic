using System;
using System.Collections.Generic;

namespace Wavee.UI.WinUI.Data.Models;

// `required` is intentionally NOT used on these models. The WinUI XAML type-
// info generator emits a parameterless `new ChangelogFeature()` for every
// `x:DataType` registration, which fails to compile when the type has
// `required` members. Defaults (`= ""`) keep nullability happy while still
// surfacing missing data visibly at runtime if a release entry is incomplete.
//
// For the same reason, members of types bound as `x:DataType` (ChangelogFeature
// and ChangelogFix) use `set`, NOT `init`: the generated type-info emits a
// property setter per bound member and won't compile against init-only members.
// ChangelogRelease is read from code-behind (not an x:DataType), so it keeps
// `init`. Do not "tidy" these back to `init` — it breaks the build.
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

    /// <summary>
    /// Optional itemized list (e.g. a "Bugfixes" section) rendered below the
    /// detail description, each row linking to its GitHub issue / PR.
    /// </summary>
    public IReadOnlyList<ChangelogFix> Fixes { get; set; } = [];
}

public sealed class ChangelogFix
{
    public string Text { get; set; } = "";

    /// <summary>Short reference label shown as the link, e.g. "#24".</summary>
    public string Reference { get; set; } = "";

    /// <summary>Target of the reference link (GitHub issue / PR). Null = no link.</summary>
    public Uri? Url { get; set; }
}
