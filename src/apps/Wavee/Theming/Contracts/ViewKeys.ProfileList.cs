// ── Theming/Contracts/ViewKeys.ProfileList.cs — the profile list page's published view key (themes plan 6.0, T10c2) ───────────────────────────
//
// T10c2 publishes the Following / Followers list page (`Pages.ProfileList`, route `people:<facet>:<user uri>`): the head (breadcrumb, page title, facet
// selector), the measured body and its skeleton region, the faces (the list, the empty, hidden, unavailable and no-match vacancies, the failure), the tools
// (chips and find box), the bound list with its letter headers, sticky letter and jump strip, the rows of cards with their Follow lines and the derived shimmer.
//
// What stays core: the demand and its retries, the pooled entry model (the filing order, the chip and the find box), the load rule that picks the face, the grid
// plan, the letter projection and its pinned extents, the sticky-letter watch, the mount cache and the list's options, and every verb (`ProfileListVerbs`).

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The Following / Followers list page. Mounted by `ProfileLists.PageHost` (the host). Props: <see cref="ProfileListProps"/>.</summary>
    public static readonly ViewKey<ProfileListProps> ProfileListPage = new("Pages.ProfileList");
}
